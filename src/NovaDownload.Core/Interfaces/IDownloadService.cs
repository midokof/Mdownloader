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
    Task RemoveAsync(Guid id);
    Task PauseAllAsync();
    Task ResumeAllAsync();
    IReadOnlyCollection<DownloadItem> Snapshot();
    IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(CancellationToken cancellationToken = default);
}
