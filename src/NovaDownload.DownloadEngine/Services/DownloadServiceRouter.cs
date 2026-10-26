using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.DownloadEngine.Services;

/// <summary>
/// Single <see cref="IDownloadService"/> entry point: picks the engine that owns a source and
/// merges both queues into one snapshot, so the UI never knows which engine produced a row.
/// </summary>
public sealed class DownloadServiceRouter : IDownloadService
{
    private readonly IDownloadService _http;
    private readonly IDownloadService _torrent;

    public DownloadServiceRouter(IDownloadService http, IDownloadService torrent)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
    }

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
        CancellationToken cancellationToken = default) =>
        (TorrentSource.IsTorrent(url) ? _torrent : _http)
        .StartDownloadAsync(url, destinationDirectory, fileName, cancellationToken);

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory,
        IReadOnlyCollection<TorrentFileInfo> selectedFiles, string? fileName = null,
        CancellationToken cancellationToken = default) =>
        (TorrentSource.IsTorrent(url) ? _torrent : _http)
        .StartDownloadAsync(url, destinationDirectory, selectedFiles, fileName, cancellationToken);

    public Task<IReadOnlyCollection<TorrentFileInfo>> LoadTorrentMetadataAsync(string url,
        CancellationToken cancellationToken = default) =>
        (TorrentSource.IsTorrent(url) ? _torrent : _http)
        .LoadTorrentMetadataAsync(url, cancellationToken);

    public IReadOnlyCollection<DownloadItem> Snapshot()
    {
        var http = _http.Snapshot();
        var torrent = _torrent.Snapshot();

        if (http.Count == 0) return torrent;
        if (torrent.Count == 0) return http;

        var merged = new List<DownloadItem>(http.Count + torrent.Count);
        merged.AddRange(http);
        merged.AddRange(torrent);
        merged.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return merged;
    }

    public async Task PauseAsync(Guid id)
    {
        await _http.PauseAsync(id).ConfigureAwait(false);
        await _torrent.PauseAsync(id).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid id)
    {
        await _http.ResumeAsync(id).ConfigureAwait(false);
        await _torrent.ResumeAsync(id).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid id)
    {
        await _http.CancelAsync(id).ConfigureAwait(false);
        await _torrent.CancelAsync(id).ConfigureAwait(false);
    }

    /// <summary>The row knows which engine produced it; only that engine restarts it.</summary>
    public async Task RestartAsync(Guid id)
    {
        var item = Snapshot().FirstOrDefault(candidate => candidate.Id == id);
        if (item == null) return;

        if (item.IsTorrent) await _torrent.RestartAsync(id).ConfigureAwait(false);
        else await _http.RestartAsync(id).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id)
    {
        await _http.RemoveAsync(id).ConfigureAwait(false);
        await _torrent.RemoveAsync(id).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, bool deleteFiles)
    {
        await _http.RemoveAsync(id, deleteFiles).ConfigureAwait(false);
        await _torrent.RemoveAsync(id, deleteFiles).ConfigureAwait(false);
    }

    public async Task SelectFilesAsync(Guid id, IReadOnlyCollection<TorrentFileInfo> selection)
    {
        await _http.SelectFilesAsync(id, selection).ConfigureAwait(false);
        await _torrent.SelectFilesAsync(id, selection).ConfigureAwait(false);
    }

    /// <summary>Restores both queues and reports the total, so startup logs one number.</summary>
    public int Restore() => _http.Restore() + _torrent.Restore();

    public async Task PauseAllAsync()
    {
        await _http.PauseAllAsync().ConfigureAwait(false);
        await _torrent.PauseAllAsync().ConfigureAwait(false);
    }

    public async Task ResumeAllAsync()
    {
        await _http.ResumeAllAsync().ConfigureAwait(false);
        await _torrent.ResumeAllAsync().ConfigureAwait(false);
    }

    public async Task CancelAllAsync()
    {
        await _http.CancelAllAsync().ConfigureAwait(false);
        await _torrent.CancelAllAsync().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in Snapshot()) yield return item;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }
}
