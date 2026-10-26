using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;
using NovaDownload.Core.Persistence;

namespace NovaDownload.App.Services;

/// <summary>
/// Single polling source of truth for the list. One tick merges the live engine rows with the
/// persisted archive, so a row that finished in a previous session still has a name, a size and a
/// category — and a removal in the UI is a removal from the archive, not a row that comes back.
/// </summary>
public sealed class DownloadStore : IDisposable
{
    private const int TickMs = 200;
    private const int SaveIntervalMs = 1000;

    private readonly IDownloadService _service;
    private readonly DownloadArchive _archive;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Archived preferences (description, category) keyed by row id, applied to live rows.</summary>
    private readonly Dictionary<Guid, DownloadRecord> _preferences = new();

    /// <summary>Last status seen per row: the edge into "downloading" is what stamps the last attempt.</summary>
    private readonly Dictionary<Guid, NovaDownload.Core.Enums.DownloadStatus> _lastStatus = new();

    private string _language = Localizer.Language;
    private string _signature = string.Empty;
    private long _lastSaveTick;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public DownloadArchive Archive => _archive;

    public event EventHandler? Refreshed;

    public DownloadStore(IDownloadService service, DownloadArchive archive)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        SeedFromArchive();

        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Rows from the previous session are on screen before the first engine tick.</summary>
    private void SeedFromArchive()
    {
        foreach (var record in _archive.Load().OrderByDescending(record => record.CreatedAt))
        {
            _preferences[record.Id] = record;
            var item = record.ToItem();
            Items.Add(item);
            _lastStatus[item.Id] = item.Status;
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var live = _service.Snapshot();

            try
            {
                await _dispatcher.InvokeAsync(() => Reconcile(live));
            }
            catch (Exception)
            {
                return; // dispatcher shut down
            }

            try
            {
                await Task.Delay(TickMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Reconcile(IReadOnlyCollection<DownloadItem> live)
    {
        var liveIds = new HashSet<Guid>(live.Count);
        foreach (var item in live) liveIds.Add(item.Id);

        // A live row that left the engine is gone; an archived row stays until the user drops it.
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var row = Items[i];
            if (!row.IsRestored && !liveIds.Contains(row.Id)) Items.RemoveAt(i);
        }

        var known = new HashSet<Guid>(Items.Count);
        foreach (var item in Items) known.Add(item.Id);

        // Snapshot is newest-first, so inserting at the head keeps the visual order stable.
        foreach (var item in live)
        {
            if (!known.Contains(item.Id))
            {
                Items.Insert(0, item);
                known.Add(item.Id);
            }
            else
            {
                // The engine restored this row: it replaces the archived copy, which only had a
                // stale progress counter. The archived description and category survive.
                var existing = Items.FirstOrDefault(row => row.Id == item.Id);
                if (existing is { IsRestored: true })
                {
                    if (_preferences.TryGetValue(item.Id, out var record)) record.ApplyPreferencesTo(item);
                    Items[Items.IndexOf(existing)] = item;
                }
            }
        }

        TrackAttempts(live);
        RankQueue(live);

        if (!string.Equals(_language, Localizer.Language, StringComparison.Ordinal))
        {
            _language = Localizer.Language;
            foreach (var item in Items) item.RefreshLocalizedText();
        }

        PersistIfDirty();

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stamps the attempt time on the edge into "downloading", never on every tick.</summary>
    private void TrackAttempts(IReadOnlyCollection<DownloadItem> live)
    {
        var seen = new HashSet<Guid>(live.Count);

        foreach (var item in live)
        {
            seen.Add(item.Id);

            if (item.Status == NovaDownload.Core.Enums.DownloadStatus.Downloading &&
                (!_lastStatus.TryGetValue(item.Id, out var before) ||
                 before != NovaDownload.Core.Enums.DownloadStatus.Downloading))
            {
                item.LastTriedAt = DateTime.UtcNow;
            }

            _lastStatus[item.Id] = item.Status;
        }

        foreach (var id in _lastStatus.Keys.Where(id => !seen.Contains(id)).ToArray())
            _lastStatus.Remove(id);
    }

    /// <summary>The Q column: waiting rows are ranked by the order they were created in.</summary>
    private static void RankQueue(IReadOnlyCollection<DownloadItem> live)
    {
        var waiting = live
            .Where(item => item.Status == NovaDownload.Core.Enums.DownloadStatus.Queued)
            .OrderBy(item => item.CreatedAt)
            .ToArray();

        for (var i = 0; i < waiting.Length; i++) waiting[i].QueuePosition = i + 1;

        foreach (var item in live)
        {
            if (item.Status != NovaDownload.Core.Enums.DownloadStatus.Queued) item.QueuePosition = 0;
        }
    }

    /// <summary>
    /// A cheap fingerprint of everything the archive shows. The file is written only when the
    /// fingerprint moves and at most once a second, so a live transfer does not rewrite the list.
    /// </summary>
    private void PersistIfDirty()
    {
        var signature = BuildSignature();
        if (signature == _signature) return;

        var now = Environment.TickCount64;
        if (_signature.Length > 0 && now - _lastSaveTick < SaveIntervalMs) return;

        _signature = signature;
        _lastSaveTick = now;

        var records = Items.Select(DownloadRecord.From).ToArray();
        var archive = _archive;
        _ = Task.Run(() => archive.Save(records));
    }

    private string BuildSignature()
    {
        var builder = new System.Text.StringBuilder(Items.Count * 48);

        foreach (var item in Items)
        {
            builder.Append(item.Id.ToString("n"))
                .Append('|').Append((int)item.Status)
                .Append('|').Append(item.ProgressBytes >> 18)
                .Append('|').Append(item.FileSize)
                .Append('|').Append(item.QueuePosition)
                .Append('|').Append((int)item.Category)
                .Append('|').Append(item.CustomCategory)
                .Append('|').Append(item.FileName)
                .Append('|').Append(item.Description)
                .Append('|').Append(item.LastTriedAt?.Ticks ?? 0)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Drops a row for good, archive included. The engine removal is the caller's job.</summary>
    public void Forget(Guid id)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Id == id) Items.RemoveAt(i);
        }

        _preferences.Remove(id);
        _lastStatus.Remove(id);
        _signature = string.Empty;
        PersistIfDirtyNow();
    }

    /// <summary>Flush used by Forget and by shutdown: the list on disk matches the list on screen.</summary>
    public void PersistIfDirtyNow()
    {
        var records = Items.Select(DownloadRecord.From).ToArray();
        _signature = BuildSignature();
        _lastSaveTick = Environment.TickCount64;
        _archive.Save(records);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();

        try { PersistIfDirtyNow(); } catch { }
    }
}
