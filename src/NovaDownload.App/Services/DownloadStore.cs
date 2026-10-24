using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.Services;

/// <summary>
/// Single polling source of truth for the UI. One tick reconciles the whole collection,
/// so removals are noticed even when the live set becomes empty — and every view reads
/// the same list, which keeps counters from drifting apart.
/// </summary>
public sealed class DownloadStore : IDisposable
{
    private const int TickMs = 200;

    private readonly IDownloadService _service;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private string _language = Localizer.Language;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public event EventHandler? Refreshed;

    public DownloadStore(IDownloadService service)
    {
        _service = service;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _loop = Task.Run(LoopAsync);
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

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!liveIds.Contains(Items[i].Id)) Items.RemoveAt(i);
        }

        var known = new HashSet<Guid>(Items.Count);
        foreach (var item in Items) known.Add(item.Id);

        // Snapshot is newest-first, so inserting at the head keeps the visual order stable.
        foreach (var item in live)
        {
            if (known.Contains(item.Id)) continue;
            Items.Insert(0, item);
            known.Add(item.Id);
        }

        // Catalog text lives on the items (status pills, metrics): re-project it once per
        // language change, on the dispatcher that owns the bindings.
        if (!string.Equals(_language, Localizer.Language, StringComparison.Ordinal))
        {
            _language = Localizer.Language;
            foreach (var item in Items) item.RefreshLocalizedText();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mirrors <paramref name="source"/> into <paramref name="target"/>, preserving order.</summary>
    public static void SyncCollection(ObservableCollection<DownloadItem> target, IReadOnlyList<DownloadItem> source)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var existing = target.IndexOf(item);
            if (existing < 0) target.Insert(i, item);
            else if (existing != i) target.Move(existing, i);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }
}
