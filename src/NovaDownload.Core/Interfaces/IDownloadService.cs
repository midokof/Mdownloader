using NovaDownload.Core.Models;

namespace NovaDownload.Core.Interfaces;

public interface IDownloadService
{
    Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null, CancellationToken cancellationToken = default);
    Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, IReadOnlyCollection<TorrentFileInfo> selectedFiles, string? fileName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<TorrentFileInfo>> LoadTorrentMetadataAsync(string url, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid id);
    Task ResumeAsync(Guid id);
    Task CancelAsync(Guid id);

    /// <summary>Starts the row over from zero: the partial payload is dropped and fetched again.</summary>
    Task RestartAsync(Guid id);
    Task RemoveAsync(Guid id);

    /// <summary>Drops the row, deciding whether the downloaded bytes stay on disk.</summary>
    Task RemoveAsync(Guid id, bool deleteFiles);

    /// <summary>
    /// Applies a file selection to a torrent. Servers have one stream, so the HTTP engine ignores it.
    /// </summary>
    Task SelectFilesAsync(Guid id, IReadOnlyCollection<TorrentFileInfo> selection);

    /// <summary>
    /// Rebuilds the unfinished rows left by a previous run, queued as paused. Returns how many came
    /// back, so the caller can log or surface the number.
    /// </summary>
    int Restore();

    Task PauseAllAsync();
    Task ResumeAllAsync();

    /// <summary>
    /// Stops every running transfer, keeps its partial data and its resume record, and marks the row
    /// <see cref="Enums.DownloadStatus.Canceled"/>: "arrêté", not "en pause".
    /// </summary>
    Task CancelAllAsync();
    IReadOnlyCollection<DownloadItem> Snapshot();
    IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(CancellationToken cancellationToken = default);
}
