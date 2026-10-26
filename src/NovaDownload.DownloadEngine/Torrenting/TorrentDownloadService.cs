using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using MonoTorrent;
using MonoTorrent.Client;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;

namespace NovaDownload.DownloadEngine.Torrenting;

/// <summary>
/// BitTorrent engine built on MonoTorrent: magnets, local .torrent files and remote .torrent URLs.
/// One <see cref="ClientEngine"/> is created lazily on the first transfer and shared by every row.
/// </summary>
public sealed class TorrentDownloadService : IDownloadService, IAsyncDisposable
{
    private const int PollMs = 500;
    private const string PartialSuffix = ".!mt";

    private readonly HttpClient _httpClient;
    private readonly TorrentEngineOptions _options;
    private readonly TorrentTransferStateStore _stateStore;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<Guid, Transfer> _transfers = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    private ClientEngine? _engine;
    private bool _disposed;

    private sealed class Transfer
    {
        public required DownloadItem Item { get; init; }

        public required CancellationTokenSource Cts { get; init; }

        /// <summary>Folder the swarm writes into; kept apart from the item so a resume never has to guess it.</summary>
        public required string DestinationDirectory { get; set; }

        /// <summary>Resume record: source, folder and the files the user kept.</summary>
        public required TorrentTransferState State { get; init; }

        public TorrentManager? Manager { get; set; }

        public Task? Worker { get; set; }

        public bool Paused { get; set; }

        public bool Detached { get; set; }

        /// <summary>Torrent-relative paths the user picked; null means every file is wanted.</summary>
        public HashSet<string>? SelectedPaths { get; set; }

        /// <summary>True once the file priorities have been written into <see cref="Manager"/>.</summary>
        public bool SelectionApplied { get; set; }
    }

    public TorrentDownloadService(HttpClient httpClient, TorrentEngineOptions options, Action<string>? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stateStore = new TorrentTransferStateStore(_options.StateDirectory);
        _log = log;
    }

    private async Task<ClientEngine> EnsureEngineAsync(CancellationToken token)
    {
        if (_engine != null) return _engine;

        await _engineGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_engine != null) return _engine;

            Directory.CreateDirectory(_options.CacheDirectory);
            var listenPort = _options.ListenPort is >= 0 and <= 65535 ? _options.ListenPort : 0;

            var settings = new EngineSettingsBuilder
            {
                AllowPortForwarding = _options.EnablePortForwarding,
                AllowLocalPeerDiscovery = _options.EnableLocalPeerDiscovery,
                AutoSaveLoadDhtCache = _options.EnableDht,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = _options.CacheDirectory,
                MaximumConnections = Math.Clamp(_options.MaxConnections, 10, 2000),
                MaximumDownloadRate = (int)TorrentEngineOptions.Limit(_options.MaxDownloadKib),
                MaximumUploadRate = (int)TorrentEngineOptions.Limit(_options.MaxUploadKib),
                UsePartialFiles = true,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, listenPort) },
                DhtEndPoint = new IPEndPoint(IPAddress.Any, listenPort),
            }.ToSettings();

            _engine = new ClientEngine(settings);
            _log?.Invoke(
                $"torrent engine started (listen port {listenPort}, dht={_options.EnableDht}, max connections {settings.MaximumConnections})");
            return _engine;
        }
        finally
        {
            _engineGate.Release();
        }
    }

    public IReadOnlyCollection<DownloadItem> Snapshot() =>
        _transfers.Values
            .Where(t => !t.Detached)
            .Select(t => t.Item)
            .OrderByDescending(i => i.CreatedAt)
            .ToArray();

    public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in Snapshot()) yield return item;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(url, destinationDirectory, fileName, selectedPaths: null, cancellationToken);

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory,
        IReadOnlyCollection<TorrentFileInfo> selectedFiles, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);

        var wanted = selectedFiles
            .Where(f => f.IsSelected)
            .Select(f => NormalizeRelative(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedFiles.Count > 0 && wanted.Count == 0)
        {
            throw new ArgumentException(
                "Select at least one file, or use the overload that downloads the whole torrent.",
                nameof(selectedFiles));
        }

        // Picking every file is the same transfer as not picking anything: keep one code path.
        var everything = selectedFiles.Count == 0 || selectedFiles.All(f => f.IsSelected);
        return StartCoreAsync(url, destinationDirectory, fileName, everything ? null : wanted, cancellationToken);
    }

    private Task<DownloadItem> StartCoreAsync(string url, string destinationDirectory, string? fileName,
        HashSet<string>? selectedPaths, CancellationToken cancellationToken)
    {
        if (!TorrentSource.IsTorrent(url))
            throw new ArgumentException("Not a magnet link, a .torrent file or a .torrent URL.", nameof(url));

        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("A destination folder is required.", nameof(destinationDirectory));

        // A live transfer of the same source is returned instead of being added twice.
        var existing = _transfers.Values.FirstOrDefault(t =>
            !t.Detached &&
            !t.Item.IsFinished &&
            string.Equals(t.Item.Url, url.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null) return Task.FromResult(existing.Item);

        Directory.CreateDirectory(destinationDirectory);

        var name = string.IsNullOrWhiteSpace(fileName) ? TorrentSource.DisplayName(url) : Sanitize(fileName);
        var item = new DownloadItem
        {
            Kind = TransferKind.Torrent,
            Url = url.Trim(),
            FileName = name,
            DestinationPath = Path.Combine(destinationDirectory, name),
            Status = DownloadStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var state = new TorrentTransferState
        {
            Id = item.Id,
            Source = item.Url,
            DestinationDirectory = Path.GetFullPath(destinationDirectory),
            DisplayName = name,
            SelectedPaths = selectedPaths?.ToList() ?? new List<string>(),
            CreatedAt = item.CreatedAt
        };
        _stateStore.Save(state);

        var transfer = new Transfer
        {
            Item = item,
            Cts = cts,
            DestinationDirectory = state.DestinationDirectory,
            State = state,
            SelectedPaths = selectedPaths
        };

        _transfers[item.Id] = transfer;
        transfer.Worker = Task.Run(() => RunAsync(transfer, cts.Token), CancellationToken.None);

        return Task.FromResult(item);
    }

    private async Task RunAsync(Transfer transfer, CancellationToken token)
    {
        var item = transfer.Item;
        try
        {
            var engine = await EnsureEngineAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            item.Status = DownloadStatus.Downloading;
            item.Phase = "Metadata";
            item.UpdatedAt = DateTime.UtcNow;

            var settings = new TorrentSettingsBuilder
            {
                AllowDht = _options.EnableDht,
                AllowPeerExchange = _options.EnablePeerExchange,
                AllowInitialSeeding = _options.SeedAfterCompletion,
                CreateContainingDirectory = true,
                MaximumConnections = Math.Clamp(_options.ConnectionsPerTorrent, 5, 500),
                MaximumDownloadRate = (int)TorrentEngineOptions.Limit(_options.MaxDownloadKib),
                MaximumUploadRate = (int)TorrentEngineOptions.Limit(_options.MaxUploadKib),
                UploadSlots = Math.Clamp(_options.UploadSlots, 1, 100),
            }.ToSettings();

            transfer.Manager = await AddAsync(engine, transfer, settings, token).ConfigureAwait(false);
            var manager = transfer.Manager;

            item.InfoHash = manager.InfoHashes.V1OrV2?.ToHex();
            await manager.StartAsync().ConfigureAwait(false);

            if (manager.Torrent == null && manager.MagnetLink != null)
            {
                using var metadataCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                metadataCts.CancelAfter(_options.MetadataTimeout);
                try
                {
                    await manager.WaitForMetadataAsync(metadataCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"No peer returned the torrent metadata within {_options.MetadataTimeout.TotalMinutes:0} minutes. " +
                        "The magnet link may have no live seeders, or DHT is unreachable from this network.");
                }
            }

            ApplyMetadata(transfer, manager);
            await ApplySelectionAsync(transfer, manager).ConfigureAwait(false);
            await PumpAsync(transfer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (item.Status is not DownloadStatus.Completed and not DownloadStatus.Canceled)
                item.Status = DownloadStatus.Paused;
            item.SpeedBps = 0;
            item.UploadSpeedBps = 0;
            item.Phase = null;
            item.UpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            item.SpeedBps = 0;
            item.UploadSpeedBps = 0;
            item.Phase = null;
            item.UpdatedAt = DateTime.UtcNow;
            _log?.Invoke($"torrent '{item.FileName}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds this swarm member. A .torrent is copied into the cache first: a resume must not depend on
    /// the file the user opened still sitting where it was.
    /// </summary>
    private async Task<TorrentManager> AddAsync(ClientEngine engine, Transfer transfer, TorrentSettings settings,
        CancellationToken token)
    {
        var state = transfer.State;

        if (TorrentSource.IsMagnet(state.Source))
        {
            var magnet = MagnetLink.Parse(state.Source.Trim());
            return await engine.AddAsync(magnet, transfer.DestinationDirectory, settings).ConfigureAwait(false);
        }

        // A previous run cached the metadata: use that copy, whatever became of the original path.
        if (!string.IsNullOrEmpty(state.TorrentFilePath) && File.Exists(state.TorrentFilePath))
        {
            var cached = Torrent.Load(state.TorrentFilePath);
            return await engine.AddAsync(cached, transfer.DestinationDirectory, settings).ConfigureAwait(false);
        }

        var torrent = await LoadTorrentFileAsync(state.Source, token, CachePathFor(state.Id)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        state.TorrentFilePath = CachePathFor(state.Id);
        _stateStore.Save(state);

        return await engine.AddAsync(torrent, transfer.DestinationDirectory, settings).ConfigureAwait(false);
    }

    private string CachePathFor(Guid id) => Path.Combine(_options.CacheDirectory, $"torrent-{id:n}.torrent");

    public async Task<IReadOnlyCollection<TorrentFileInfo>> LoadTorrentMetadataAsync(string url,
        CancellationToken cancellationToken = default)
    {
        if (!TorrentSource.IsTorrent(url))
            throw new ArgumentException("Not a magnet link, a .torrent file or a .torrent URL.", nameof(url));

        if (TorrentSource.IsMagnet(url))
        {
            // A magnet carries no file list: the swarm has to answer first. The row reports the
            // files as soon as the metadata arrives, so the picker can read them from there.
            throw new NotSupportedException(
                "A magnet link has no file list until a peer returns the metadata. " +
                "Add it to the queue and pick the files from the row once the swarm answers.");
        }

        var torrent = await LoadTorrentFileAsync(url, cancellationToken).ConfigureAwait(false);
        return torrent.Files
            .Select(f => new TorrentFileInfo(f.Path, f.Length))
            .ToArray();
    }

    private async Task<Torrent> LoadTorrentFileAsync(string source, CancellationToken token, string? cacheAs = null)
    {
        if (TorrentSource.IsRemoteTorrentFile(source))
        {
            Directory.CreateDirectory(_options.CacheDirectory);
            var cachePath = cacheAs ?? Path.Combine(_options.CacheDirectory, $"meta-{Guid.NewGuid():N}.torrent");
            var keep = cacheAs != null;
            try
            {
                var torrent = await Torrent.LoadAsync(_httpClient, new Uri(source.Trim()), cachePath)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var fetchedBytes = new FileInfo(cachePath).Length;
                if (fetchedBytes > _options.MaxTorrentFileBytes)
                {
                    throw new InvalidDataException(
                        $"The .torrent fetched from {new Uri(source.Trim()).Host} is {Formatters.Bytes(fetchedBytes)}, " +
                        $"larger than the {Formatters.Bytes(_options.MaxTorrentFileBytes)} limit for a torrent file.");
                }

                _log?.Invoke($"torrent metadata fetched from {source}");
                return torrent;
            }
            finally
            {
                // The cache copy is the resume source; anything else was only a parsing stop-over.
                if (!keep)
                {
                    try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
                }
            }
        }

        var path = source.Trim().Trim('"');
        if (!File.Exists(path)) throw new FileNotFoundException("Torrent file not found: " + path, path);

        var length = new FileInfo(path).Length;
        if (length > _options.MaxTorrentFileBytes)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is {Formatters.Bytes(length)}, larger than the " +
                $"{Formatters.Bytes(_options.MaxTorrentFileBytes)} limit for a torrent file.");
        }

        var loaded = Torrent.Load(path);

        if (cacheAs != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheAs) ?? _options.CacheDirectory);
            File.Copy(path, cacheAs, overwrite: true);
        }

        return loaded;
    }

    private void ApplyMetadata(Transfer transfer, TorrentManager manager)
    {
        var item = transfer.Item;
        var torrent = manager.Torrent;
        if (torrent == null) return;

        item.InfoHash = torrent.InfoHashes.V1OrV2?.ToHex();
        item.FileSize = torrent.Size;
        item.FileName = string.IsNullOrWhiteSpace(torrent.Name) ? item.FileName : Sanitize(torrent.Name);

        var single = torrent.Files.Count == 1;
        item.DestinationPath = single
            ? Path.Combine(transfer.DestinationDirectory,
                torrent.Files[0].Path.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(transfer.DestinationDirectory, item.FileName);

        // The row owns the file list so the UI can show it without asking the engine again, and it
        // remembers the user's choice so the picker reopens on the same ticks.
        var selected = transfer.SelectedPaths;
        item.TorrentFiles.Clear();
        foreach (var file in manager.Files)
        {
            var wanted = selected == null || selected.Contains(NormalizeRelative(file.Path));
            item.TorrentFiles.Add(new TorrentFileInfo(file.Path, file.Length, wanted));
        }

        item.NotifyTorrentFilesChanged();

        transfer.State.DisplayName = item.FileName;
        _stateStore.Save(transfer.State);
    }

    /// <summary>Writes the user's file choice into the manager before the first piece is requested.</summary>
    private async Task ApplySelectionAsync(Transfer transfer, TorrentManager manager)
    {
        if (transfer.SelectedPaths == null || manager.Torrent == null || manager.Files.Count <= 1) return;

        var matched = 0;
        foreach (var file in manager.Files)
        {
            var wanted = transfer.SelectedPaths.Contains(NormalizeRelative(file.Path));
            if (wanted) matched++;
            await manager.SetFilePriorityAsync(file, wanted ? Priority.Normal : Priority.DoNotDownload)
                .ConfigureAwait(false);
        }

        if (matched == 0)
        {
            throw new InvalidOperationException(
                "None of the selected files are part of this torrent: the metadata changed since the selection was made.");
        }

        transfer.SelectionApplied = true;
        _log?.Invoke($"torrent '{transfer.Item.FileName}': {matched}/{manager.Files.Count} files selected");
    }

    private async Task PumpAsync(Transfer transfer, CancellationToken token)
    {
        var item = transfer.Item;
        var manager = transfer.Manager!;
        var completedReported = false;

        while (!token.IsCancellationRequested)
        {
            if (transfer.Paused)
            {
                item.SpeedBps = 0;
                item.UploadSpeedBps = 0;
                await Delay(token).ConfigureAwait(false);
                continue;
            }

            var torrent = manager.Torrent;
            var state = manager.State;

            if (torrent != null)
            {
                item.FileSize = TransferSize(manager, torrent);
                if (!IsVerifying(state))
                {
                    // PartialProgress honours the file priorities; for a full download it equals Progress.
                    var percent = Math.Clamp(manager.PartialProgress, 0, 100);
                    item.ProgressBytes = Math.Clamp(
                        (long)Math.Round(percent / 100.0 * item.FileSize), 0, item.FileSize);
                }
            }

            var monitor = manager.Monitor;
            item.SpeedBps = monitor.DownloadRate;
            item.UploadSpeedBps = monitor.UploadRate;
            item.UploadedBytes = monitor.DataBytesSent;

            var seeds = Math.Max(0, manager.Peers.Seeds);
            var connections = Math.Max(0, manager.OpenConnections);
            item.Seeds = seeds;
            item.Leechers = Math.Max(manager.Peers.Leechs, connections - seeds);
            item.ConnectedPeers = connections;
            item.AvailablePeers = Math.Max(0, manager.Peers.Available);
            item.Ratio = Ratio(monitor.DataBytesSent, monitor.DataBytesReceived, item.FileSize);
            item.Phase = state.ToString();
            item.UpdatedAt = DateTime.UtcNow;

            switch (state)
            {
                case TorrentState.Error:
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = manager.Error?.Exception?.Message
                                        ?? manager.Error?.Reason.ToString()
                                        ?? "The swarm reported an error.";
                    item.SpeedBps = 0;
                    item.UploadSpeedBps = 0;
                    _log?.Invoke($"torrent '{item.FileName}' stopped: {item.ErrorMessage}");
                    return;

                case TorrentState.Starting:
                case TorrentState.Hashing:
                case TorrentState.HashingPaused:
                case TorrentState.Metadata:
                case TorrentState.FetchingHashes:
                    item.Status = DownloadStatus.Downloading;
                    break;

                case TorrentState.Downloading:
                case TorrentState.Seeding:
                    // A partial selection never sets Complete (the skipped pieces stay missing
                    // forever); PartialProgress counts only the pieces the user kept, so it is the
                    // honest finish line for a selection as well as for a whole torrent.
                    if (SwarmFinished(manager) && DataIsSettled(transfer))
                    {
                        item.Status = DownloadStatus.Completed;
                        item.ErrorMessage = null;
                        if (!completedReported)
                        {
                            completedReported = true;
                            item.ProgressBytes = item.FileSize;
                            _log?.Invoke($"torrent '{item.FileName}' complete");

                            // Nothing left to resume: drop the JSON and the cached metadata.
                            SettleState(transfer);

                            if (!_options.SeedAfterCompletion)
                            {
                                await manager.StopAsync().ConfigureAwait(false);
                                item.SpeedBps = 0;
                                item.UploadSpeedBps = 0;
                                item.Phase = TorrentState.Stopped.ToString();
                                return;
                            }
                        }
                    }
                    else
                    {
                        item.Status = DownloadStatus.Downloading;
                        completedReported = false;
                    }
                    break;

                case TorrentState.Stopped:
                case TorrentState.Paused:
                    if (item.Status != DownloadStatus.Completed) item.Status = DownloadStatus.Paused;
                    break;
            }

            await Delay(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True once every piece the user asked for is on disk. <see cref="TorrentManager.Complete"/>
    /// only ever turns true when the whole torrent is present, which a partial selection never is.
    /// </summary>
    private static bool SwarmFinished(TorrentManager manager)
    {
        if (!manager.HasMetadata) return false;
        if (manager.Complete) return true;

        var partial = manager.Files.Any(f => f.Priority == Priority.DoNotDownload);
        return partial && manager.PartialProgress >= 100.0;
    }

    /// <summary>Bytes the current selection is expected to produce.</summary>
    private static long TransferSize(TorrentManager manager, Torrent torrent)
    {
        if (!manager.Files.Any(f => f.Priority == Priority.DoNotDownload)) return torrent.Size;

        var wanted = 0L;
        foreach (var file in manager.Files.Where(f => f.Priority != Priority.DoNotDownload))
            wanted += file.Length;
        return wanted > 0 ? wanted : torrent.Size;
    }

    private static bool IsVerifying(TorrentState state) => state
        is TorrentState.Starting
        or TorrentState.Hashing
        or TorrentState.HashingPaused
        or TorrentState.Metadata
        or TorrentState.FetchingHashes;

    /// <summary>MonoTorrent writes an incremental "partial" marker while it is still moving bytes.</summary>
    private static bool DataIsSettled(Transfer transfer)
    {
        var destinationPath = transfer.Item.DestinationPath;
        if (string.IsNullOrWhiteSpace(destinationPath)) return true;

        var folder = Directory.Exists(destinationPath) ? destinationPath : Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return true;

        try
        {
            return !Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(path => path.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static Task Delay(CancellationToken token) => Task.Delay(PollMs, token);

    private static double Ratio(long uploaded, long downloaded, long torrentSize)
    {
        var total = Math.Max(downloaded, torrentSize);
        return total > 0 ? Math.Round((double)uploaded / total, 2) : 0;
    }

    public async Task PauseAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        if (transfer.Item.Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Paused)
            return;

        transfer.Paused = true;
        transfer.Item.Status = DownloadStatus.Paused;
        transfer.Item.SpeedBps = 0;
        transfer.Item.UploadSpeedBps = 0;
        transfer.Item.UpdatedAt = DateTime.UtcNow;

        // Pausing is the natural moment to record the selection: the next run replays it exactly.
        _stateStore.Save(transfer.State);

        var manager = transfer.Manager;
        if (manager == null) return;

        try
        {
            await manager.PauseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"torrent pause failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the paused rows from the JSON files left by a previous run. Nothing is started here:
    /// the user decides, and the resume itself costs a hash check of what is already on disk.
    /// </summary>
    public int Restore()
    {
        var restored = 0;

        foreach (var state in _stateStore.LoadAll())
        {
            if (_transfers.ContainsKey(state.Id)) continue;

            var displayName = string.IsNullOrWhiteSpace(state.DisplayName)
                ? TorrentSource.DisplayName(state.Source)
                : state.DisplayName;

            var item = new DownloadItem
            {
                Id = state.Id,
                Kind = TransferKind.Torrent,
                Url = state.Source,
                FileName = displayName,
                DestinationPath = Path.Combine(state.DestinationDirectory, displayName),
                Status = DownloadStatus.Paused,
                CreatedAt = state.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                Phase = "Restored"
            };

            var selected = state.SelectedPaths.Count > 0
                ? state.SelectedPaths.Select(NormalizeRelative).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            _transfers[state.Id] = new Transfer
            {
                Item = item,
                Cts = new CancellationTokenSource(),
                DestinationDirectory = state.DestinationDirectory,
                State = state,
                SelectedPaths = selected
            };

            restored++;
        }

        if (restored > 0) _log?.Invoke($"{restored} torrent(s) restored from disk");
        return restored;
    }

    /// <summary>
    /// Applies a file selection to a torrent that is already running, and records it for the next
    /// run. This is what makes a magnet usable: its file list only exists once a peer answers.
    /// </summary>
    public async Task SelectFilesAsync(Guid id, IReadOnlyCollection<TorrentFileInfo> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var wanted = selection
            .Where(f => f.IsSelected)
            .Select(f => NormalizeRelative(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
            throw new ArgumentException("Keep at least one file, or remove the torrent.", nameof(selection));

        transfer.SelectedPaths = wanted;
        transfer.State.SelectedPaths = wanted.ToList();
        _stateStore.Save(transfer.State);

        foreach (var info in transfer.Item.TorrentFiles)
            info.IsSelected = wanted.Contains(NormalizeRelative(info.Path));

        var manager = transfer.Manager;
        if (manager?.Torrent == null || manager.Files.Count <= 1) return;

        foreach (var file in manager.Files)
        {
            await manager.SetFilePriorityAsync(
                    file, wanted.Contains(NormalizeRelative(file.Path)) ? Priority.Normal : Priority.DoNotDownload)
                .ConfigureAwait(false);
        }

        transfer.SelectionApplied = true;
        _log?.Invoke($"torrent '{transfer.Item.FileName}': selection changed to {wanted.Count} file(s)");
    }

    public async Task ResumeAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer) || !transfer.Item.CanResume) return;

        transfer.Paused = false;
        transfer.Item.Status = DownloadStatus.Queued;
        transfer.Item.ErrorMessage = null;
        transfer.Item.SpeedBps = 0;
        transfer.Item.UpdatedAt = DateTime.UtcNow;

        if (transfer.Manager == null || transfer.Worker == null || transfer.Worker.IsCompleted)
        {
            Restart(transfer);
            return;
        }

        try
        {
            await transfer.Manager.StartAsync().ConfigureAwait(false);
            transfer.Item.Status = DownloadStatus.Downloading;
        }
        catch (Exception ex)
        {
            transfer.Item.Status = DownloadStatus.Failed;
            transfer.Item.ErrorMessage = ex.Message;
            _log?.Invoke($"torrent resume failed: {ex.Message}");
        }
    }

    private void Restart(Transfer transfer)
    {
        var item = transfer.Item;

        var cts = new CancellationTokenSource();
        transfer.Cts.Dispose();

        var fresh = new Transfer
        {
            Item = item,
            Cts = cts,
            DestinationDirectory = transfer.DestinationDirectory,
            State = transfer.State,
            SelectedPaths = transfer.SelectedPaths,
        };

        _transfers[item.Id] = fresh;
        fresh.Worker = Task.Run(() => RunAsync(fresh, cts.Token), CancellationToken.None);
    }

    /// <summary>Drops the resume record and the cached metadata of a transfer that will not come back.</summary>
    private void SettleState(Transfer transfer)
    {
        _stateStore.Delete(transfer.State.Id);

        var cached = transfer.State.TorrentFilePath;
        if (string.IsNullOrEmpty(cached)) return;

        try { if (File.Exists(cached)) File.Delete(cached); } catch { }
    }

    public async Task CancelAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var item = transfer.Item;
        item.Status = DownloadStatus.Canceled;
        item.SpeedBps = 0;
        item.UploadSpeedBps = 0;
        item.Phase = null;
        item.UpdatedAt = DateTime.UtcNow;

        try { transfer.Cts.Cancel(); } catch { }

        if (transfer.Worker != null)
        {
            try { await Task.WhenAny(transfer.Worker, Task.Delay(5000)).ConfigureAwait(false); } catch { }
        }

        if (transfer.Manager != null && _engine != null)
        {
            try
            {
                await StopQuietlyAsync(transfer.Manager).ConfigureAwait(false);
                if (transfer.Manager.State is not TorrentState.Stopping)
                {
                    await _engine.RemoveAsync(transfer.Manager, RemoveMode.DownloadedDataOnly).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ex.Message.Contains("Stopping state", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke($"torrent removal failed: {ex.Message}");
            }
            transfer.Manager = null;
        }

        SettleState(transfer);
        transfer.Detached = true;
    }

    /// <summary>"Retélécharger": the payload and the resume record go, the source comes back.</summary>
    public async Task RestartAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var source = transfer.Item.Url;
        var directory = transfer.DestinationDirectory;

        await RemoveAsync(id, deleteFiles: true).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(directory)) return;
        await StartDownloadAsync(source, directory).ConfigureAwait(false);
    }

    public Task RemoveAsync(Guid id) => RemoveAsync(id, deleteFiles: false);

    /// <summary>
    /// Drops the row. <paramref name="deleteFiles"/> decides what happens to the payload: a finished
    /// torrent keeps its data by default, and an unfinished one always loses its partial pieces.
    /// </summary>
    public async Task RemoveAsync(Guid id, bool deleteFiles)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var finished = DataIsSettled(transfer) &&
                       ((transfer.Manager?.Complete ?? false) || transfer.Item.Status == DownloadStatus.Completed);

        if (!finished)
        {
            // CancelAsync already removes the partial payload and settles the state file.
            await CancelAsync(id).ConfigureAwait(false);
            Forget(id);
            return;
        }

        if (transfer.Manager != null)
        {
            try
            {
                await StopQuietlyAsync(transfer.Manager).ConfigureAwait(false);
                if (transfer.Manager.State is not TorrentState.Stopping && _engine != null)
                {
                    await _engine.RemoveAsync(transfer.Manager,
                            deleteFiles ? RemoveMode.DownloadedDataOnly : RemoveMode.KeepAllData)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ex.Message.Contains("Stopping state", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke($"torrent detach failed: {ex.Message}");
            }

            transfer.Manager = null;
        }

        SettleState(transfer);
        try { transfer.Cts.Cancel(); } catch { }
        Forget(id);
    }

    private static async Task StopQuietlyAsync(TorrentManager manager)
    {
        var waited = 0;
        while (manager.State == TorrentState.Stopping && waited < 10000)
        {
            await Task.Delay(100).ConfigureAwait(false);
            waited += 100;
        }

        if (manager.State is TorrentState.Stopped or TorrentState.Stopping) return;

        try
        {
            await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Forget(Guid id)
    {
        if (!_transfers.TryRemove(id, out var transfer)) return;
        transfer.Detached = true;
        try { transfer.Cts.Dispose(); } catch { }
    }

    public async Task PauseAllAsync()
    {
        foreach (var id in _transfers.Keys.ToArray())
            await PauseAsync(id).ConfigureAwait(false);
    }

    /// <summary>
    /// "Tout arrêter": the swarm session stops and the row reads "arrêté". The resume record written
    /// by the pause path stays on disk, so nothing has to be fetched twice.
    /// </summary>
    public async Task CancelAllAsync()
    {
        foreach (var id in _transfers.Keys.ToArray())
        {
            if (!_transfers.TryGetValue(id, out var transfer)) continue;
            if (transfer.Item.Status is not (DownloadStatus.Downloading or DownloadStatus.Queued)) continue;

            await PauseAsync(id).ConfigureAwait(false);

            transfer.Item.Status = DownloadStatus.Canceled;
            transfer.Item.SpeedBps = 0;
            transfer.Item.UploadSpeedBps = 0;
            transfer.Item.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var id in _transfers.Keys.ToArray())
            await ResumeAsync(id).ConfigureAwait(false);
    }

    /// <summary>Pushes the rate and connection limits into a session that is already running.</summary>
    public async Task ApplyOptionsAsync(TorrentEngineOptions options)
    {
        _options.MaxDownloadKib = options.MaxDownloadKib;
        _options.MaxUploadKib = options.MaxUploadKib;
        _options.SeedAfterCompletion = options.SeedAfterCompletion;

        var engine = _engine;
        if (engine == null) return;

        var port = engine.Settings.ListenEndPoints.TryGetValue("ipv4", out var endpoint)
            ? endpoint.Port
            : _options.ListenPort;

        var settings = new EngineSettingsBuilder(engine.Settings)
        {
            MaximumDownloadRate = (int)TorrentEngineOptions.Limit(options.MaxDownloadKib),
            MaximumUploadRate = (int)TorrentEngineOptions.Limit(options.MaxUploadKib),
            MaximumConnections = Math.Clamp(options.MaxConnections, 10, 2000),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, port) },
        }.ToSettings();

        try
        {
            await engine.UpdateSettingsAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"torrent settings update failed: {ex.Message}");
        }
    }

    public (int Torrents, long DownloadRate, long UploadRate, int Connections)? SessionInfo()
    {
        var engine = _engine;
        if (engine == null) return null;

        return (engine.Torrents.Count, engine.TotalDownloadRate, engine.TotalUploadRate,
            engine.ConnectionManager?.OpenConnections ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var transfer in _transfers.Values)
        {
            try { transfer.Cts.Cancel(); } catch { }
            transfer.Detached = true;
        }

        var engine = _engine;
        _engine = null;

        if (engine != null)
        {
            try { await engine.SaveStateAsync().ConfigureAwait(false); } catch { }
            try { await engine.StopAllAsync().ConfigureAwait(false); } catch { }
            try { engine.Dispose(); } catch { }
        }

        _engineGate.Dispose();
    }

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string Sanitize(string name)
    {
        var value = Path.GetFileName(name.Trim().Trim('"'));
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        value = value.Trim().Trim('.');
        return value.Length == 0 ? "torrent" : value;
    }
}
