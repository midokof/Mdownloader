using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;

namespace NovaDownload.UnitTests.Browser;

/// <summary>Records what the processor asked the engine to do, without touching the network.</summary>
internal sealed class RecordingDownloadService : IDownloadService
{
    public List<DownloadItem> Items { get; } = new();

    public List<(string Url, string Directory, string? FileName)> Started { get; } = new();

    public Exception? StartFailure { get; set; }

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        if (StartFailure != null) throw StartFailure;

        var resolved = string.IsNullOrWhiteSpace(fileName) ? "resolved.bin" : fileName!;
        var item = new DownloadItem
        {
            Url = url,
            FileName = resolved,
            DestinationPath = Path.Combine(destinationDirectory, resolved),
            Status = DownloadStatus.Downloading
        };

        Items.Add(item);
        Started.Add((url, destinationDirectory, fileName));
        return Task.FromResult(item);
    }

    public void Seed(DownloadItem item) => Items.Add(item);

    public Task PauseAsync(Guid id) => Task.CompletedTask;
    public Task ResumeAsync(Guid id) => Task.CompletedTask;
    public Task CancelAsync(Guid id) => Task.CompletedTask;
    public Task RemoveAsync(Guid id) => Task.CompletedTask;
    public Task PauseAllAsync() => Task.CompletedTask;
    public Task ResumeAllAsync() => Task.CompletedTask;
    public IReadOnlyCollection<DownloadItem> Snapshot() => Items.ToArray();

    public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

internal sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(AppSettings? settings = null) => Current = settings ?? new AppSettings();

    public AppSettings Current { get; }

    public Task LoadAsync() => Task.CompletedTask;

    public Task SaveAsync() => Task.CompletedTask;
}
