using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;

namespace NovaDownload.IntegrationTests;

public class DownloadEngineTests
{
    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return data;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HttpDownloadService Engine(int concurrency = 3) =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, concurrency);

    private static async Task WaitForStatusAsync(DownloadItem item, DownloadStatus status, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (item.Status == status) return;
            if (item.Status == DownloadStatus.Failed && status != DownloadStatus.Failed)
                throw new Xunit.Sdk.XunitException($"Download failed: {item.ErrorMessage}");
            await Task.Delay(40);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {status}; last status {item.Status} ({item.ErrorMessage}).");
    }

    [Fact]
    public async Task Download_Completes_And_Writes_Exact_Bytes()
    {
        var payload = Payload(1_200_000);
        await using var server = new TestHttpServer(payload);
        var dir = TempDir();

        var item = await Engine().StartDownloadAsync(server.Url, dir);
        await WaitForStatusAsync(item, DownloadStatus.Completed);

        var path = item.DestinationPath;
        Assert.True(File.Exists(path), "destination file missing");
        Assert.Equal(payload.Length, new FileInfo(path).Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".npart"), "temporary file left behind");
        Assert.Equal(100, item.ProgressPercent, 3);
    }

    [Fact]
    public async Task Pause_Then_Resume_Continues_From_Offset_And_Sends_Range()
    {
        var payload = Payload(4_000_000);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 8);
        var dir = TempDir();

        var service = Engine();
        var item = await service.StartDownloadAsync(server.Url, dir);

        var spin = DateTime.UtcNow.AddSeconds(15);
        while (item.ProgressBytes < 250_000 && DateTime.UtcNow < spin) await Task.Delay(20);
        Assert.True(item.ProgressBytes > 0, "transfer never started");

        await service.PauseAsync(item.Id);
        await Task.Delay(350);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        var temp = item.DestinationPath + ".npart";
        var pausedBytes = new FileInfo(temp).Length;
        Assert.True(pausedBytes > 0, "nothing was written before pause");

        await Task.Delay(350);
        Assert.Equal(pausedBytes, new FileInfo(temp).Length);

        await service.ResumeAsync(item.Id);
        await WaitForStatusAsync(item, DownloadStatus.Completed, 30000);

        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Contains(server.RangeHeaders, r => r.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Name_Conflict_Appends_Suffix_And_Does_Not_Overwrite()
    {
        var payload = Payload(200_000);
        await using var server = new TestHttpServer(payload) { FileName = "conflict.bin" };
        var dir = TempDir();

        var existing = Path.Combine(dir, "conflict.bin");
        await File.WriteAllTextAsync(existing, "keep me");

        var item = await Engine().StartDownloadAsync(server.Url, dir);
        await WaitForStatusAsync(item, DownloadStatus.Completed);

        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        Assert.Equal("conflict (1).bin", Path.GetFileName(item.DestinationPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    [Fact]
    public async Task Cancel_Removes_Partial_File()
    {
        var payload = Payload(3_000_000);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 10);
        var dir = TempDir();

        var service = Engine();
        var item = await service.StartDownloadAsync(server.Url, dir);

        var spin = DateTime.UtcNow.AddSeconds(10);
        while (item.ProgressBytes == 0 && DateTime.UtcNow < spin) await Task.Delay(20);

        await service.CancelAsync(item.Id);
        await Task.Delay(150);

        Assert.Equal(DownloadStatus.Canceled, item.Status);
        Assert.False(File.Exists(item.DestinationPath + ".npart"), "partial file should be removed on cancel");
        Assert.False(File.Exists(item.DestinationPath), "no final file after cancel");
    }

    [Fact]
    public async Task Remove_Of_Completed_Item_Drops_The_Record_And_Keeps_The_File()
    {
        var payload = Payload(300_000);
        await using var server = new TestHttpServer(payload);
        var service = Engine();
        var dir = TempDir();

        var item = await service.StartDownloadAsync(server.Url, dir);
        await WaitForStatusAsync(item, DownloadStatus.Completed);

        await service.RemoveAsync(item.Id);

        Assert.Empty(service.Snapshot());
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.True(File.Exists(item.DestinationPath), "removing a record must never delete the finished file");
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    [Fact]
    public async Task Clear_Finished_Empties_The_List_When_Every_Transfer_Is_Done()
    {
        var payload = Payload(150_000);
        await using var server = new TestHttpServer(payload);
        var service = Engine();
        var dir = TempDir();

        var first = await service.StartDownloadAsync(server.Url, dir);
        await WaitForStatusAsync(first, DownloadStatus.Completed);

        // The regression that made "Clear finished" look dead: with a single item, no new
        // snapshot entries arrive, so a reconcile driven only by arrivals never purges.
        foreach (var item in service.Snapshot().Where(i => i.IsFinished).ToArray())
            await service.RemoveAsync(item.Id);

        Assert.Empty(service.Snapshot());
    }

    [Fact]
    public async Task Queued_Transfer_Can_Be_Paused_Before_It_Starts()
    {
        var payload = Payload(4_000_000);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 10);
        var service = Engine(concurrency: 1);
        var dir = TempDir();

        var running = await service.StartDownloadAsync(server.Url, dir);
        var waiting = await service.StartDownloadAsync(server.Url, dir);

        var spin = DateTime.UtcNow.AddSeconds(10);
        while (running.Status != DownloadStatus.Downloading && DateTime.UtcNow < spin) await Task.Delay(20);
        Assert.Equal(DownloadStatus.Downloading, running.Status);
        Assert.Equal(DownloadStatus.Queued, waiting.Status);

        await service.PauseAsync(waiting.Id);
        await Task.Delay(400);

        Assert.Equal(DownloadStatus.Paused, waiting.Status);
        Assert.Equal(0, waiting.ProgressBytes);
        Assert.True(waiting.CanResume);

        await service.CancelAsync(running.Id);
        await service.CancelAsync(waiting.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/file.bin")]
    [InlineData("not-a-url")]
    public async Task Invalid_Url_Is_Rejected(string url)
    {
        var service = Engine();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartDownloadAsync(url, TempDir()));
    }

    [Fact]
    public async Task Snapshot_And_Remove_Track_State()
    {
        var payload = Payload(120_000);
        await using var server = new TestHttpServer(payload);
        var service = Engine();
        var dir = TempDir();

        var item = await service.StartDownloadAsync(server.Url, dir);
        Assert.Contains(service.Snapshot(), d => d.Id == item.Id);

        await WaitForStatusAsync(item, DownloadStatus.Completed);
        await service.RemoveAsync(item.Id);

        Assert.DoesNotContain(service.Snapshot(), d => d.Id == item.Id);
        Assert.True(File.Exists(Path.Combine(dir, "fixture.bin")), "removing from the list must not delete the file");
    }
}
