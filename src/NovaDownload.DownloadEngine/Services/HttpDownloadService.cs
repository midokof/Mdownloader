// language: C#, file: HttpDownloadService.cs, target: .NET 9
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Http;

namespace NovaDownload.DownloadEngine.Services;

/// <summary>
/// HTTP/HTTPS engine. A transfer is cut into byte ranges fetched in parallel, each one resuming from
/// its own offset, with a JSON state file per download so a restart of the application continues
/// where the previous run stopped. Servers that refuse ranges fall back to a single stream.
/// </summary>
public sealed class HttpDownloadService : IDownloadService, IDisposable
{
    private const int BufferSize = 81920;
    private const int ReportIntervalMs = 160;
    private const int StateSaveIntervalMs = 2000;

    private readonly HttpClient _directClient;
    private readonly HttpEngineOptions _options;
    private readonly HttpTransferStateStore _store;
    private readonly GlobalRateLimiter _limiter = new();
    private readonly ConcurrentDictionary<Guid, Transfer> _transfers = new();
    private readonly SemaphoreSlim _scheduler;

    private HttpClient? _proxyClient;
    private bool _disposed;

    public HttpDownloadService(HttpClient httpClient, int maxConcurrentDownloads = 3,
        HttpEngineOptions? options = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new HttpEngineOptions();
        _store = new HttpTransferStateStore(_options.StateDirectory);
        _scheduler = new SemaphoreSlim(Math.Max(1, maxConcurrentDownloads));
        _limiter.BytesPerSecond = _options.GlobalSpeedLimitBps;
    }

    private sealed class Transfer
    {
        public required DownloadItem Item { get; init; }

        public required HttpTransferState State { get; init; }

        public required CancellationTokenSource Cts { get; set; }

        public Task? Worker { get; set; }

        /// <summary>
        /// Incremented by every explicit restart or resume. A worker that was superseded while it
        /// was unwinding must not overwrite the status of the attempt that replaced it.
        /// </summary>
        public int Generation { get; set; }

        public bool Paused { get; set; }

        /// <summary>Open writer, or null while nothing is being written. Used to flush before a state save.</summary>
        public FileStream? Sink { get; set; }

        /// <summary>The gate that serialises writes to <see cref="Sink"/>.</summary>
        public SemaphoreSlim? WriteGate { get; set; }
    }

    /// <summary>Per-transfer byte meter, written by every segment and read by the reporter.</summary>
    private sealed class Counter
    {
        public long Value;
    }

    private readonly record struct ProbeResult(
        long TotalSize,
        bool SupportsRange,
        bool Empty,
        string? ETag,
        string? LastModified,
        string? ServerName);

    // ---- configuration ---------------------------------------------------------

    /// <summary>Total allowance in bytes per second; 0 lifts the limit. Applies to running transfers too.</summary>
    public long GlobalSpeedLimitBps
    {
        get => _limiter.BytesPerSecond;
        set
        {
            _options.GlobalSpeedLimitBps = Math.Max(0, value);
            _limiter.BytesPerSecond = _options.GlobalSpeedLimitBps;
        }
    }

    public HttpEngineOptions Options => _options;

    /// <summary>Switches proxy mode. An already built proxy client is thrown away, the next request uses the new one.</summary>
    public void ApplyProxy(HttpProxyMode mode, string? host = null, int port = 0)
    {
        if (mode == HttpProxyMode.Manual &&
            (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535))
        {
            throw new ArgumentException("A manual proxy needs a host and a port between 1 and 65535.", nameof(host));
        }

        _options.ProxyMode = mode;
        _options.ProxyHost = host;
        _options.ProxyPort = port;

        var stale = _proxyClient;
        _proxyClient = null;
        stale?.Dispose();
    }

    private HttpClient Client
    {
        get
        {
            if (_options.ProxyMode == HttpProxyMode.Direct) return _directClient;
            return _proxyClient ??= BuildProxyClient();
        }
    }

    private HttpClient BuildProxyClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = true
        };

        handler.Proxy = _options.ProxyMode == HttpProxyMode.Manual
            ? new WebProxy(_options.ProxyHost!, _options.ProxyPort) { BypassProxyOnLocal = false }
            : HttpClient.DefaultProxy;

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromHours(1) };
    }

    // ---- queue surface ---------------------------------------------------------

    public IReadOnlyCollection<DownloadItem> Snapshot() =>
        _transfers.Values.Select(t => t.Item).OrderByDescending(i => i.CreatedAt).ToArray();

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
        CancellationToken cancellationToken = default) =>
        Start(new HttpDownloadRequest(url, destinationDirectory, fileName), cancellationToken);

    /// <summary>Start with per-download credentials; the plain overload forwards here with none.</summary>
    public Task<DownloadItem> StartAsync(HttpDownloadRequest request, CancellationToken cancellationToken = default) =>
        Start(request, cancellationToken);

    private Task<DownloadItem> Start(HttpDownloadRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS URLs are supported.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
            throw new ArgumentException("A destination folder is required.", nameof(request));

        Directory.CreateDirectory(request.DestinationDirectory);
        _limiter.BytesPerSecond = _options.GlobalSpeedLimitBps;

        var pinned = SafeFileName(request.FileName);
        var name = pinned.Length > 0 ? pinned : SafeFileName(Path.GetFileName(uri.LocalPath));
        if (name.Length == 0) name = "download";

        var destination = UniquePath(request.DestinationDirectory, name);

        var item = new DownloadItem
        {
            Kind = TransferKind.Http,
            Url = request.Url,
            FileName = Path.GetFileName(destination),
            DestinationPath = destination,
            Status = DownloadStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var state = new HttpTransferState
        {
            Id = item.Id,
            Url = item.Url,
            DestinationPath = destination,
            TempPath = destination + ".npart",
            FileName = item.FileName,
            PinnedName = pinned.Length > 0,
            UserName = request.UserName,
            Password = request.Password,
            CreatedAt = item.CreatedAt
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var transfer = new Transfer { Item = item, State = state, Cts = cts };
        _transfers[item.Id] = transfer;
        transfer.Worker = Task.Run(() => RunAsync(transfer, transfer.Generation, cts.Token), CancellationToken.None);

        return Task.FromResult(item);
    }

    /// <summary>
    /// Rebuilds the paused rows from the JSON state files left by a previous run. Progress, size and
    /// validators come from the file, so the list shows real numbers before anything is resumed.
    /// </summary>
    public int Restore()
    {
        var restored = 0;

        foreach (var state in _store.LoadAll())
        {
            if (_transfers.ContainsKey(state.Id)) continue;

            var written = state.WrittenBytes;
            if (!File.Exists(state.TempPath))
            {
                // The partial file is gone, so the state describes nothing that can be continued.
                _store.Delete(state.Id);
                continue;
            }

            var item = new DownloadItem
            {
                Id = state.Id,
                Kind = TransferKind.Http,
                Url = state.Url,
                FileName = state.FileName,
                DestinationPath = state.DestinationPath,
                FileSize = state.TotalSize,
                ProgressBytes = written,
                Status = DownloadStatus.Paused,
                SupportsRange = state.SupportsRange,
                ETag = state.ETag,
                LastModified = state.LastModified,
                ConnectionCount = Math.Max(1, state.Segments.Count),
                CreatedAt = state.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };

            _transfers[state.Id] = new Transfer
            {
                Item = item,
                State = state,
                Cts = new CancellationTokenSource()
            };

            restored++;
        }

        return restored;
    }

    private async Task RunAsync(Transfer transfer, int generation, CancellationToken token)
    {
        var acquired = false;

        try
        {
            await _scheduler.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            if (transfer.Generation != generation) return;

            transfer.Item.Status = DownloadStatus.Downloading;
            transfer.Item.ErrorMessage = null;
            transfer.Item.UpdatedAt = DateTime.UtcNow;

            await TransferAsync(transfer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (transfer.Generation != generation) return;

            if (transfer.Item.Status != DownloadStatus.Canceled)
                transfer.Item.Status = DownloadStatus.Paused;

            transfer.Item.SpeedBps = 0;
            transfer.Item.UpdatedAt = DateTime.UtcNow;

            if (transfer.Item.Status != DownloadStatus.Canceled && transfer.State.IsPlanned)
                _store.Save(transfer.State);
        }
        catch (Exception ex)
        {
            if (transfer.Generation != generation) return;

            transfer.Item.Status = DownloadStatus.Failed;
            transfer.Item.ErrorMessage = ex.Message;
            transfer.Item.SpeedBps = 0;
            transfer.Item.UpdatedAt = DateTime.UtcNow;

            if (transfer.State.IsPlanned) _store.Save(transfer.State);
        }
        finally
        {
            if (acquired) _scheduler.Release();
        }
    }

    /// <summary>Full lifecycle of one attempt: probe, plan or validate, fetch every range, assemble.</summary>
    private async Task TransferAsync(Transfer transfer, CancellationToken token)
    {
        var state = transfer.State;
        var item = transfer.Item;

        var resuming = state.IsPlanned;
        var probe = await ProbeAsync(transfer, token).ConfigureAwait(false);

        if (resuming)
        {
            ValidateAgainstProbe(state, probe);

            // The server answered with the same validators: refresh what may have been missing.
            state.ETag = probe.ETag ?? state.ETag;
            state.LastModified = probe.LastModified ?? state.LastModified;
        }
        else
        {
            ApplyProbe(transfer, probe);
        }

        if (probe.Empty)
        {
            // 416 on the very first range or a declared zero length: the object is empty.
            await File.WriteAllBytesAsync(state.TempPath, Array.Empty<byte>(), token).ConfigureAwait(false);
            Finalize(transfer);
            return;
        }

        if (!File.Exists(state.TempPath))
        {
            // Nothing survived on disk: resume from zero rather than trust the bookkeeping.
            foreach (var segment in state.Segments) segment.Written = 0;
        }

        if (AllSegmentsComplete(state))
        {
            Finalize(transfer);
            return;
        }

        EnsureDiskSpace(state.DestinationPath, Math.Max(0, state.TotalSize - state.WrittenBytes));

        var counter = new Counter { Value = state.WrittenBytes };
        var writeGate = new SemaphoreSlim(1, 1);
        var sink = new FileStream(state.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            BufferSize, useAsync: true);

        if (state.TotalSize > 0) sink.SetLength(state.TotalSize);

        transfer.Sink = sink;
        transfer.WriteGate = writeGate;

        using var reportCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            var reporter = ReportAsync(transfer, counter, reportCts.Token);

            try
            {
                var workers = state.Segments
                    .Select(segment => RunSegmentAsync(transfer, segment, sink, writeGate, counter, token))
                    .ToArray();

                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                reportCts.Cancel();
                try { await reporter.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            transfer.Sink = null;
            transfer.WriteGate = null;
            await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            sink.Dispose();
            writeGate.Dispose();
        }

        item.ProgressBytes = counter.Value;
        Finalize(transfer);
    }

    // ---- probe -----------------------------------------------------------------

    /// <summary>
    /// Two bytes are enough to learn everything that matters: whether ranges are honoured, the full
    /// size, and the validators that make a later resume safe. 416 means the object is empty.
    /// </summary>
    private async Task<ProbeResult> ProbeAsync(Transfer transfer, CancellationToken token)
    {
        var attempt = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                return await ProbeOnceAsync(transfer.State, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < _options.MaxRetries)
            {
                attempt++;
                transfer.Item.RetryCount = attempt;
                await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProbeResult> ProbeOnceAsync(HttpTransferState state, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, state.Url);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        ApplyAuth(request, state);

        using var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return new ProbeResult(0, true, true, ETagOf(response), LastModifiedOf(response), ServerNameOf(response));

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            var total = range?.Length ?? (range?.To is { } to ? to + 1 : 0);
            return new ProbeResult(total, true, total == 0, ETagOf(response), LastModifiedOf(response), ServerNameOf(response));
        }

        response.EnsureSuccessStatusCode();

        // No range support: the body is the whole file. A missing Content-Length means chunked,
        // which is "size unknown", not "empty" - the object may well hold gigabytes.
        var contentLength = response.Content.Headers.ContentLength;
        var length = contentLength ?? 0;
        return new ProbeResult(length, false, contentLength.HasValue && length == 0,
            ETagOf(response), LastModifiedOf(response), ServerNameOf(response));
    }

    private void ApplyProbe(Transfer transfer, ProbeResult probe)
    {
        var state = transfer.State;
        var item = transfer.Item;

        state.TotalSize = probe.TotalSize;
        state.SupportsRange = probe.SupportsRange;
        state.ETag = probe.ETag;
        state.LastModified = probe.LastModified;

        item.SupportsRange = state.SupportsRange;
        item.ETag = state.ETag;
        item.LastModified = state.LastModified;
        item.FileSize = state.TotalSize;

        ApplyServerName(transfer, probe.ServerName);
        PlanSegments(state);

        item.ConnectionCount = Math.Max(1, state.Segments.Count);
    }

    /// <summary>Content-Disposition wins over the URL segment, unless the caller pinned a name.</summary>
    private static void ApplyServerName(Transfer transfer, string? serverName)
    {
        var state = transfer.State;
        if (state.PinnedName) return;

        var name = SafeFileName(serverName);
        if (name.Length == 0) return;
        if (string.Equals(name, state.FileName, StringComparison.OrdinalIgnoreCase)) return;

        var directory = Path.GetDirectoryName(state.DestinationPath);
        if (string.IsNullOrEmpty(directory)) return;

        // Nothing has been written yet, so the partial file simply follows the new name.
        var destination = UniquePath(directory, name);
        state.DestinationPath = destination;
        state.FileName = Path.GetFileName(destination);
        state.TempPath = destination + ".npart";

        transfer.Item.DestinationPath = destination;
        transfer.Item.FileName = state.FileName;
    }

    private void PlanSegments(HttpTransferState state)
    {
        state.Segments.Clear();

        if (state.TotalSize <= 0)
        {
            // Unknown size (chunked transfer): one stream, and every resume starts from zero.
            state.SupportsRange = false;
            state.Segments.Add(new HttpSegmentState { Index = 0, Start = 0, End = -1 });
            return;
        }

        if (!state.SupportsRange)
        {
            state.Segments.Add(new HttpSegmentState { Index = 0, Start = 0, End = state.TotalSize - 1 });
            return;
        }

        var floor = Math.Max(1, _options.MinimumSegmentBytes);
        var wanted = (long)Math.Clamp(_options.Segments, 1, 64);
        var count = state.TotalSize >= floor
            ? (int)Math.Clamp(state.TotalSize / floor, 1, wanted)
            : 1;

        var baseSize = state.TotalSize / count;
        var remainder = state.TotalSize % count;

        var cursor = 0L;
        for (var i = 0; i < count; i++)
        {
            var length = baseSize + (i < remainder ? 1 : 0);
            state.Segments.Add(new HttpSegmentState { Index = i, Start = cursor, End = cursor + length - 1 });
            cursor += length;
        }
    }

    // ---- resume safety ---------------------------------------------------------

    /// <summary>
    /// Refuses to continue when the remote object is not the one the bytes came from. A silent append
    /// would produce a file that matches neither version.
    /// </summary>
    private static void ValidateAgainstProbe(HttpTransferState state, ProbeResult probe)
    {
        if (state.SupportsRange && !probe.SupportsRange)
        {
            throw new RemoteChangedException(
                "The server stopped honouring byte ranges, so the parts already downloaded cannot be completed. " +
                "Restart the download to fetch the whole file in one stream.");
        }

        if (state.TotalSize > 0 && probe.TotalSize > 0 && state.TotalSize != probe.TotalSize)
        {
            throw new RemoteChangedException(
                $"The remote file is now {Formatters.Bytes(probe.TotalSize)} instead of {Formatters.Bytes(state.TotalSize)}. " +
                "Restart the download to fetch the new version.");
        }

        if (!string.IsNullOrEmpty(state.ETag) && !string.IsNullOrEmpty(probe.ETag) &&
            !SameValidator(state.ETag, probe.ETag))
        {
            throw new RemoteChangedException(
                $"The remote file changed (ETag {state.ETag} became {probe.ETag}). " +
                "Restart the download to fetch the new version.");
        }

        if (!string.IsNullOrEmpty(state.LastModified) && !string.IsNullOrEmpty(probe.LastModified) &&
            !string.Equals(state.LastModified, probe.LastModified, StringComparison.Ordinal))
        {
            throw new RemoteChangedException(
                $"The remote file changed (last modified {probe.LastModified}). " +
                "Restart the download to fetch the new version.");
        }
    }

    /// <summary>Weak and strong ETags never match each other; the W/ prefix is not part of the value.</summary>
    private static bool SameValidator(string left, string right)
    {
        static string Bare(string value) => value.Trim().Replace("W/", string.Empty, StringComparison.Ordinal);
        return string.Equals(Bare(left), Bare(right), StringComparison.Ordinal);
    }

    // ---- segments --------------------------------------------------------------

    private async Task RunSegmentAsync(Transfer transfer, HttpSegmentState segment, FileStream sink,
        SemaphoreSlim writeGate, Counter counter, CancellationToken token)
    {
        var attempt = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (segment.Length >= 0 && segment.Written >= segment.Length) return;

            try
            {
                if (await FetchSegmentAsync(transfer, segment, sink, writeGate, counter, token).ConfigureAwait(false))
                    return;

                throw new IOException(
                    $"The server closed the connection after {Formatters.Bytes(segment.Written)} " +
                    $"of {Formatters.Bytes(segment.Length)}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                attempt++;
                if (attempt > _options.MaxRetries) throw;

                transfer.Item.RetryCount = attempt;
                await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One request for one range, resuming at <c>Start + Written</c>. True when the range is complete.</summary>
    private async Task<bool> FetchSegmentAsync(Transfer transfer, HttpSegmentState segment, FileStream sink,
        SemaphoreSlim writeGate, Counter counter, CancellationToken token)
    {
        var state = transfer.State;
        var knownLength = segment.Length >= 0;
        var from = segment.Start + segment.Written;

        using var request = new HttpRequestMessage(HttpMethod.Get, state.Url);

        if (state.SupportsRange)
        {
            request.Headers.Range = knownLength
                ? new RangeHeaderValue(from, segment.End)
                : new RangeHeaderValue(from, null);

            var validator = IfRangeValidator(state);
            if (validator != null) request.Headers.TryAddWithoutValidation("If-Range", validator);
        }

        ApplyAuth(request, state);

        using var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (state.SupportsRange && response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    "The server answered a byte-range request with the whole file. Restart the download.");
            }

            response.EnsureSuccessStatusCode();
        }

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        var moved = 0L;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var want = buffer.Length;
            if (knownLength)
            {
                var remaining = segment.Start + segment.Length - (from + moved);
                if (remaining <= 0) break;
                want = (int)Math.Min(buffer.Length, remaining);
            }

            var read = await source.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
            if (read == 0) break;

            // The allowance is charged before the bytes land, so the disk never outruns the budget.
            await _limiter.AcquireAsync(read, token).ConfigureAwait(false);

            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                sink.Position = from + moved;
                await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                segment.Written += read;
                moved += read;
            }
            finally
            {
                writeGate.Release();
            }

            Interlocked.Add(ref counter.Value, read);
        }

        return !knownLength || segment.Written >= segment.Length;
    }

    private static bool AllSegmentsComplete(HttpTransferState state)
    {
        if (state.Segments.Count == 0) return false;

        foreach (var segment in state.Segments)
        {
            if (segment.Length < 0) return false;
            if (segment.Written < segment.Length) return false;
        }

        return true;
    }

    /// <summary>Progress, speed and the periodic state save, driven off the shared byte counter.</summary>
    private async Task ReportAsync(Transfer transfer, Counter counter, CancellationToken token)
    {
        var item = transfer.Item;
        var previous = Interlocked.Read(ref counter.Value);
        var previousTick = Environment.TickCount64;
        var lastSave = previousTick;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReportIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = Environment.TickCount64;
            var total = Interlocked.Read(ref counter.Value);
            var elapsed = (now - previousTick) / 1000.0;

            if (elapsed > 0)
            {
                var instant = (total - previous) / elapsed;
                item.SpeedBps = item.SpeedBps <= 0 ? instant : item.SpeedBps * 0.7 + instant * 0.3;
            }

            item.ProgressBytes = total;
            item.UpdatedAt = DateTime.UtcNow;

            previous = total;
            previousTick = now;

            if (now - lastSave >= StateSaveIntervalMs)
            {
                await PersistAsync(transfer, token).ConfigureAwait(false);
                lastSave = now;
            }
        }

        item.SpeedBps = 0;
    }

    /// <summary>
    /// Writes the state file only after the bytes it counts have left the process buffer, and holds
    /// the write gate across both so a segment cannot report progress that is still unwritten. This
    /// is what makes a killed process resumable: the JSON never claims more than the disk holds.
    /// </summary>
    private async Task PersistAsync(Transfer transfer, CancellationToken token)
    {
        var gate = transfer.WriteGate;
        var sink = transfer.Sink;

        if (gate == null || sink == null)
        {
            _store.Save(transfer.State);
            return;
        }

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await sink.FlushAsync(token).ConfigureAwait(false);
            _store.Save(transfer.State);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- assembly --------------------------------------------------------------

    private void Finalize(Transfer transfer)
    {
        var state = transfer.State;
        var item = transfer.Item;

        var directory = Path.GetDirectoryName(state.DestinationPath) ?? ".";
        if (File.Exists(state.DestinationPath))
        {
            var unique = UniquePath(directory, Path.GetFileName(state.DestinationPath));
            state.DestinationPath = unique;
            item.DestinationPath = unique;
            item.FileName = Path.GetFileName(unique);
        }

        File.Move(state.TempPath, state.DestinationPath, overwrite: true);

        item.FileSize = new FileInfo(state.DestinationPath).Length;
        item.ProgressBytes = item.FileSize;
        item.SpeedBps = 0;
        item.ConnectionCount = Math.Max(1, state.Segments.Count);
        item.Status = DownloadStatus.Completed;
        item.ErrorMessage = null;
        item.UpdatedAt = DateTime.UtcNow;

        _store.Delete(item.Id);

        // The transfer is over: keep the row, drop the resume machinery.
        state.Segments.Clear();
    }

    // ---- control ---------------------------------------------------------------

    public async Task PauseAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var item = transfer.Item;
        if (item.Status is not (DownloadStatus.Downloading or DownloadStatus.Queued)) return;

        // Queued transfers land straight in Paused: the wait on the scheduler is cancelled too.
        try { transfer.Cts.Cancel(); } catch (ObjectDisposedException) { }

        transfer.Paused = true;
        item.Status = DownloadStatus.Paused;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;

        // The worker may already be assembling the last segment: wait for it to settle, so a row
        // never reports "Paused" over a file Finalize just moved into place and de-registered.
        // When this returns the outcome is final: either Paused with a resume record, or Completed.
        if (transfer.Worker is { IsCompleted: false })
        {
            try { await Task.WhenAny(transfer.Worker, Task.Delay(5000)).ConfigureAwait(false); } catch { }
        }
    }

    public async Task ResumeAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var item = transfer.Item;
        if (!item.CanResume) return;

        // The previous attempt may still be unwinding: it has to release the partial file before
        // the next one starts writing at the offsets it just reported.
        if (transfer.Worker is { IsCompleted: false })
        {
            try { await Task.WhenAny(transfer.Worker, Task.Delay(5000)).ConfigureAwait(false); } catch { }
        }

        try { transfer.Cts.Dispose(); } catch { }

        var cts = new CancellationTokenSource();
        transfer.Cts = cts;
        transfer.Paused = false;

        var generation = ++transfer.Generation;

        item.Status = DownloadStatus.Queued;
        item.ErrorMessage = null;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;

        _limiter.BytesPerSecond = _options.GlobalSpeedLimitBps;
        transfer.Worker = Task.Run(() => RunAsync(transfer, generation, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Throws away the bytes and the state of a transfer and starts it again from zero. This is the
    /// deliberate answer to <see cref="RemoteChangedException"/>.
    /// </summary>
    public async Task RestartAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        try { transfer.Cts.Cancel(); } catch (ObjectDisposedException) { }

        if (transfer.Worker != null)
        {
            try { await Task.WhenAny(transfer.Worker, Task.Delay(3000)).ConfigureAwait(false); } catch { }
        }

        try { if (File.Exists(transfer.State.TempPath)) File.Delete(transfer.State.TempPath); } catch { }
        _store.Delete(id);

        transfer.State.Segments.Clear();
        transfer.State.TotalSize = 0;
        transfer.State.ETag = null;
        transfer.State.LastModified = null;

        try { transfer.Cts.Dispose(); } catch { }

        var cts = new CancellationTokenSource();
        transfer.Cts = cts;

        var generation = ++transfer.Generation;

        var item = transfer.Item;
        item.Status = DownloadStatus.Queued;
        item.ProgressBytes = 0;
        item.FileSize = 0;
        item.ErrorMessage = null;
        item.RetryCount = 0;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;

        transfer.Worker = Task.Run(() => RunAsync(transfer, generation, cts.Token), CancellationToken.None);
    }

    public async Task CancelAsync(Guid id)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var item = transfer.Item;
        item.Status = DownloadStatus.Canceled;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;

        try { transfer.Cts.Cancel(); } catch (ObjectDisposedException) { }

        // The writer has to let go of the handle before the partial file can be unlinked.
        if (transfer.Worker != null)
        {
            try { await Task.WhenAny(transfer.Worker, Task.Delay(3000)).ConfigureAwait(false); } catch { }
        }

        try { if (File.Exists(transfer.State.TempPath)) File.Delete(transfer.State.TempPath); } catch { }
        _store.Delete(id);
    }

    public Task RemoveAsync(Guid id) => RemoveAsync(id, deleteFiles: false);

    /// <summary>
    /// Drops the row. <paramref name="deleteFiles"/> also unlinks the finished file; an unfinished
    /// transfer loses its partial data either way, because nobody asked to keep a broken piece.
    /// </summary>
    public async Task RemoveAsync(Guid id, bool deleteFiles)
    {
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        if (!transfer.Item.IsFinished)
            await CancelAsync(id).ConfigureAwait(false);

        if (deleteFiles)
        {
            var path = transfer.Item.DestinationPath;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by another process: the row still leaves the list.
            }
        }

        _store.Delete(id);
        Forget(id);
    }

    /// <summary>A server object is one stream: file selection does not apply.</summary>
    public Task SelectFilesAsync(Guid id, IReadOnlyCollection<TorrentFileInfo> selection) => Task.CompletedTask;

    private void Forget(Guid id)
    {
        if (!_transfers.TryRemove(id, out var transfer)) return;
        try { transfer.Cts.Dispose(); } catch { }
    }

    public async Task PauseAllAsync()
    {
        foreach (var id in _transfers.Keys.ToArray())
            await PauseAsync(id).ConfigureAwait(false);
    }

    /// <summary>
    /// "Tout arrêter": every running transfer stops and reads "arrêté". The pause path has already
    /// written a valid state file, so the row keeps its bytes and resumes on demand.
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
            transfer.Item.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var id in _transfers.Keys.ToArray())
            await ResumeAsync(id).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in Snapshot()) yield return item;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyCollection<TorrentFileInfo>> LoadTorrentMetadataAsync(string url,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Torrent metadata is only available from the BitTorrent engine.");

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory,
        IReadOnlyCollection<TorrentFileInfo> selectedFiles, string? fileName = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Torrent sources are not supported by the HTTP engine.");

    // ---- helpers ---------------------------------------------------------------

    private static void ApplyAuth(HttpRequestMessage request, HttpTransferState state)
    {
        if (string.IsNullOrEmpty(state.UserName)) return;

        var raw = $"{state.UserName}:{state.Password ?? string.Empty}";
        request.Headers.TryAddWithoutValidation(
            "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }

    private static string? IfRangeValidator(HttpTransferState state)
    {
        if (!string.IsNullOrEmpty(state.ETag) && !state.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return state.ETag;

        return state.LastModified;
    }

    private static string? ETagOf(HttpResponseMessage response) => response.Headers.ETag?.Tag;

    private static string? LastModifiedOf(HttpResponseMessage response) =>
        response.Content.Headers.LastModified?.ToString("R");

    private static string? ServerNameOf(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        if (disposition == null) return null;
        return disposition.FileNameStar ?? disposition.FileName;
    }

    private TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(8000, Math.Max(1, _options.RetryBaseDelayMs) * Math.Pow(2, Math.Max(0, attempt - 1))));

    private static bool IsRetryable(Exception ex) => ex switch
    {
        RemoteChangedException => false,
        OperationCanceledException => false,
        HttpRequestException { StatusCode: { } code } => code is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout,
        HttpRequestException => true,
        IOException => true,
        TimeoutException => true,
        _ => false
    };

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0) return;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Not enough free space on {drive.Name}: {Formatters.Bytes(drive.AvailableFreeSpace)} available, " +
                    $"{Formatters.Bytes(requiredBytes)} required.");
            }
        }
        catch (IOException) { throw; }
        catch
        {
            // Probing a drive is best effort; a wrong guess must not block a download.
        }
    }

    private static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        name = Path.GetFileName(name.Trim().Trim('"'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name is "." or "..") return string.Empty;

        return name.Trim();
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var transfer in _transfers.Values)
        {
            try { transfer.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        _proxyClient?.Dispose();
        _proxyClient = null;
        _scheduler.Dispose();
    }
}
