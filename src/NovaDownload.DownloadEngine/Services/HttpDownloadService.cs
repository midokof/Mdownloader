using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;

namespace NovaDownload.DownloadEngine.Services;

/// <summary>
/// Async HTTP/HTTPS download engine. Segmented-ready single-stream implementation with
/// resumable transfers (HTTP Range), throttled progress reporting and bounded concurrency.
/// </summary>
public class HttpDownloadService : IDownloadService
{
    private const int BufferSize = 81920;
    private const int ProgressIntervalMs = 160;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, DownloadItem> _downloads = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cts = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();
    private readonly SemaphoreSlim _scheduler;

    public HttpDownloadService(HttpClient httpClient, int maxConcurrentDownloads = 3)
    {
        _httpClient = httpClient;
        _scheduler = new SemaphoreSlim(Math.Max(1, maxConcurrentDownloads));
    }

    public IReadOnlyCollection<DownloadItem> Snapshot() =>
        _downloads.Values.OrderByDescending(d => d.CreatedAt).ToArray();

    public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS URLs are supported.", nameof(url));
        }

        Directory.CreateDirectory(destinationDirectory);

        var name = SafeFileName(fileName);
        if (string.IsNullOrWhiteSpace(name)) name = SafeFileName(Path.GetFileName(uri.LocalPath));
        if (string.IsNullOrWhiteSpace(name)) name = "download";

        var destination = UniquePath(destinationDirectory, name);

        var item = new DownloadItem
        {
            Url = url,
            FileName = Path.GetFileName(destination),
            DestinationPath = destination,
            Status = DownloadStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _downloads[item.Id] = item;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts[item.Id] = cts;
        _tasks[item.Id] = Task.Run(() => RunAsync(item, cts.Token), CancellationToken.None);

        return Task.FromResult(item);
    }

    private async Task RunAsync(DownloadItem item, CancellationToken token)
    {
        var acquired = false;
        try
        {
            await _scheduler.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            item.Status = DownloadStatus.Downloading;
            item.UpdatedAt = DateTime.UtcNow;

            await TransferAsync(item, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (item.Status != DownloadStatus.Canceled)
                item.Status = DownloadStatus.Paused;
            item.SpeedBps = 0;
            item.UpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            item.SpeedBps = 0;
            item.UpdatedAt = DateTime.UtcNow;
        }
        finally
        {
            if (acquired) _scheduler.Release();
        }
    }

    private async Task TransferAsync(DownloadItem item, CancellationToken token)
    {
        var tempPath = item.DestinationPath + ".npart";
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        if (existing > 0 && item.FileSize > 0 && existing >= item.FileSize)
        {
            // Already complete on disk from a previous run.
            Finalize(tempPath, item);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);

        // Only resume when we can prove the remote object is unchanged.
        var canResume = existing > 0 && item.SupportsRange &&
                        (item.ETag != null || item.LastModified != null);

        if (canResume)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            if (item.ETag != null && !item.ETag.StartsWith("W/"))
                request.Headers.TryAddWithoutValidation("If-Range", item.ETag);
            else if (item.LastModified != null)
                request.Headers.TryAddWithoutValidation("If-Range", item.LastModified);
        }
        else if (existing > 0)
        {
            // Cannot prove safety: start over rather than append blindly.
            try { File.Delete(tempPath); } catch { }
            existing = 0;
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            try { File.Delete(tempPath); } catch { }
            throw new IOException("The server rejected the resume range. Restart the download.");
        }

        response.EnsureSuccessStatusCode();

        var partial = response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && !partial)
        {
            // Server ignored the range -> full body. Restart cleanly.
            existing = 0;
            try { File.Delete(tempPath); } catch { }
        }

        item.SupportsRange = response.Headers.AcceptRanges.Contains("bytes") || partial;
        item.ETag = response.Headers.ETag?.Tag ?? item.ETag;
        if (response.Content.Headers.LastModified.HasValue)
            item.LastModified = response.Content.Headers.LastModified.Value.ToString("R");

        var remoteLength = response.Content.Headers.ContentLength;
        if (remoteLength.HasValue)
            item.FileSize = partial ? existing + remoteLength.Value : remoteLength.Value;

        // Pick up server-declared filename when the caller did not pin one.
        var cd = response.Content.Headers.ContentDisposition;
        if (cd != null && string.IsNullOrEmpty(item.FileName))
        {
            var serverName = SafeFileName(cd.FileNameStar ?? cd.FileName);
            if (!string.IsNullOrWhiteSpace(serverName)) item.FileName = serverName;
        }

        EnsureDiskSpace(item.DestinationPath, Math.Max(0, item.FileSize - existing));

        var mode = partial && existing > 0 ? FileMode.Append : FileMode.Create;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var sink = new FileStream(tempPath, mode, FileAccess.Write, FileShare.None, BufferSize, true);

        var buffer = new byte[BufferSize];
        long total = existing;
        var lastReport = Environment.TickCount64;
        var lastBytes = total;

        item.ProgressBytes = total;

        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;

            await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;

            var now = Environment.TickCount64;
            if (now - lastReport >= ProgressIntervalMs)
            {
                var elapsed = (now - lastReport) / 1000.0;
                if (elapsed > 0)
                {
                    var instant = (total - lastBytes) / elapsed;
                    item.SpeedBps = item.SpeedBps <= 0 ? instant : item.SpeedBps * 0.7 + instant * 0.3;
                }
                item.ProgressBytes = total;
                item.UpdatedAt = DateTime.UtcNow;
                lastReport = now;
                lastBytes = total;
            }
        }

        await sink.FlushAsync(token).ConfigureAwait(false);
        sink.Close();

        item.ProgressBytes = total;
        Finalize(tempPath, item);
    }

    private static void Finalize(string tempPath, DownloadItem item)
    {
        if (File.Exists(item.DestinationPath))
        {
            var dir = Path.GetDirectoryName(item.DestinationPath) ?? ".";
            item.DestinationPath = UniquePath(dir, Path.GetFileName(item.DestinationPath));
            item.FileName = Path.GetFileName(item.DestinationPath);
        }

        File.Move(tempPath, item.DestinationPath, overwrite: true);
        item.ProgressBytes = item.FileSize > 0 ? item.FileSize : new FileInfo(item.DestinationPath).Length;
        item.FileSize = item.ProgressBytes;
        item.SpeedBps = 0;
        item.Status = DownloadStatus.Completed;
        item.ErrorMessage = null;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                throw new IOException(
                    $"Not enough free space on {drive.Name}: {Formatters.Bytes(drive.AvailableFreeSpace)} available, {Formatters.Bytes(requiredBytes)} required.");
        }
        catch (IOException) { throw; }
        catch { /* drive probing is best-effort */ }
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
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return path;
    }

    public Task PauseAsync(Guid id)
    {
        // Queued transfers can be paused too: the token is already cancelled, so RunAsync
        // never reaches Downloading and lands straight in the Paused branch.
        if (_downloads.TryGetValue(id, out var item) &&
            item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
        {
            if (_cts.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            item.Status = DownloadStatus.Paused;
            item.SpeedBps = 0;
            item.UpdatedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(Guid id)
    {
        if (!_downloads.TryGetValue(id, out var item)) return Task.CompletedTask;
        if (!item.CanResume) return Task.CompletedTask;

        if (_cts.TryGetValue(id, out var old))
        {
            try { old.Dispose(); } catch { }
        }

        var cts = new CancellationTokenSource();
        _cts[id] = cts;
        item.Status = DownloadStatus.Queued;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;
        _tasks[id] = Task.Run(() => RunAsync(item, cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task CancelAsync(Guid id)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;

        if (_cts.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }

        item.Status = DownloadStatus.Canceled;
        item.SpeedBps = 0;
        item.UpdatedAt = DateTime.UtcNow;

        // Let the writer release its handle before the partial file is unlinked.
        if (_tasks.TryGetValue(id, out var task))
        {
            try { await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false); } catch { }
        }

        var tempPath = item.DestinationPath + ".npart";
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }

    public async Task RemoveAsync(Guid id)
    {
        if (!_downloads.TryGetValue(id, out var item)) return;

        // A finished transfer is only a record: drop it without rewriting its status or
        // touching the file on disk.
        if (item.IsFinished)
        {
            Forget(id);
            return;
        }

        await CancelAsync(id).ConfigureAwait(false);
        Forget(id);
    }

    private void Forget(Guid id)
    {
        _downloads.TryRemove(id, out _);
        if (_cts.TryRemove(id, out var cts)) { try { cts.Dispose(); } catch { } }
        _tasks.TryRemove(id, out _);
    }

    public async Task PauseAllAsync()
    {
        foreach (var id in _downloads.Keys.ToArray())
            await PauseAsync(id).ConfigureAwait(false);
    }

    public async Task ResumeAllAsync()
    {
        foreach (var id in _downloads.Keys.ToArray())
            await ResumeAsync(id).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in _downloads.Values) yield return item;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }
}
