using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Http;
using NovaDownload.DownloadEngine.Services;
using Xunit.Abstractions;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// Step 2 contract: segmented transfers, resume inside a session, resume after the process is gone,
/// servers that refuse ranges, chunked responses, remote change detection, and the global limiter.
/// Every assertion on the result is a SHA-256 comparison against the bytes the server holds.
/// </summary>
public class HttpSegmentedTests
{
    private readonly ITestOutputHelper _output;

    public HttpSegmentedTests(ITestOutputHelper output) => _output = output;

    private static byte[] Payload(int size, int seed = 0)
    {
        var data = new byte[size];
        new Random(size + seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string ShaFile(string path) => Sha(File.ReadAllBytes(path));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-http", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HttpDownloadService Engine(string stateDirectory, int concurrency = 3,
        Action<HttpEngineOptions>? configure = null)
    {
        var options = new HttpEngineOptions { StateDirectory = stateDirectory };
        configure?.Invoke(options);
        return new HttpDownloadService(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, concurrency, options);
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(40);
        }

        throw new Xunit.Sdk.XunitException($"Timed out: {message}");
    }

    private static Task WaitAsync(DownloadItem item, DownloadStatus status, int timeoutMs = 60000) =>
        WaitAsync(() => item.Status == status, timeoutMs,
            $"waiting for {status}; last {item.Status} ({item.ErrorMessage})");

    private static int StateFileCount(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;

    // ---- b) segmented download ------------------------------------------------

    [Fact]
    public async Task Segmented_Download_Produces_The_Exact_Sha256()
    {
        var payload = Payload(8 * 1024 * 1024);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 2);
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.Equal(8, item.ConnectionCount);
        Assert.True(server.MaxConcurrentRequests >= 8,
            $"expected eight overlapping requests, saw {server.MaxConcurrentRequests}");
        Assert.Equal(8, server.RangeHeaders.Count(r => r != "bytes=0-1"));

        // The probe asks for two bytes: the cheapest question that reveals range support and size.
        Assert.Equal("bytes=0-1", server.RangeHeaders[0]);
    }

    [Fact]
    public async Task Small_File_Stays_On_A_Single_Stream()
    {
        var payload = Payload(300_000);
        await using var server = new TestHttpServer(payload);
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.Equal(1, item.ConnectionCount);
    }

    // ---- c) pause then resume -------------------------------------------------

    [Fact]
    public async Task Pause_Then_Resume_Produces_The_Exact_Sha256()
    {
        var payload = Payload(8 * 1024 * 1024);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 25);
        var state = TempDir();
        var service = Engine(state);

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(() => item.ProgressBytes > 1_000_000, 30000, "the transfer to pass 1 MB");

        // PauseAsync settles the worker before returning: the status below is final, and a fixture
        // that still managed to finish is a loud failure instead of a mysterious 10 s wait.
        await service.PauseAsync(item.Id);
        Assert.True(item.Status == DownloadStatus.Paused,
            $"the transfer finished before the pause could land (status {item.Status})");

        var paused = item.ProgressBytes;
        Assert.True(paused > 0, "nothing was written before the pause");
        Assert.Equal(paused, item.ProgressBytes);

        await service.ResumeAsync(item.Id);
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.False(File.Exists(item.DestinationPath + ".npart"), "the partial file survived completion");
        Assert.Equal(0, StateFileCount(state));
    }

    // ---- d) resume after the process is gone ----------------------------------

    [Fact]
    public async Task Resume_After_A_Restart_Produces_The_Exact_Sha256()
    {
        var payload = Payload(6 * 1024 * 1024);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 25);
        var state = TempDir();
        var destination = TempDir();

        var first = Engine(state);
        var item = await first.StartDownloadAsync(server.Url, destination);
        await WaitAsync(() => item.ProgressBytes > 1_000_000, 30000, "the transfer to pass 1 MB");

        await first.PauseAsync(item.Id);
        Assert.True(item.Status == DownloadStatus.Paused,
            $"the transfer finished before the pause could land (status {item.Status})");
        Assert.Equal(1, StateFileCount(state));

        var reached = item.ProgressBytes;
        first.Dispose();

        // A brand new service with no memory of the first one: only the JSON file survives.
        var second = Engine(state);
        Assert.Equal(1, second.Restore());

        var restored = Assert.Single(second.Snapshot());
        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.True(restored.ProgressBytes > 0, "the restored row lost its progress");
        Assert.Equal(payload.Length, restored.FileSize);
        Assert.True(restored.ProgressBytes >= reached,
            $"the restored row went backwards: {restored.ProgressBytes} < {reached}");
        Assert.True(restored.ProgressBytes < payload.Length, "the restored row claims a finished transfer");

        await second.ResumeAsync(restored.Id);
        await WaitAsync(restored, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(restored.DestinationPath));
        Assert.Equal(0, StateFileCount(state));
    }

    // ---- e) server without range support --------------------------------------

    [Fact]
    public async Task Server_Without_Range_Support_Still_Produces_The_Exact_Sha256()
    {
        var payload = Payload(3 * 1024 * 1024);
        await using var server = new TestHttpServer(payload) { SupportsRange = false };
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.Equal(1, item.ConnectionCount);
        Assert.False(item.SupportsRange);

        // The probe was answered with the whole body: no range request ever left the client.
        Assert.Empty(server.RangeHeaders);
    }

    // ---- f) chunked response ---------------------------------------------------

    [Fact]
    public async Task Chunked_Server_Produces_The_Exact_Sha256()
    {
        var payload = Payload(2 * 1024 * 1024);
        await using var server = new TestHttpServer(payload) { SupportsRange = false, UseChunked = true };
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.Equal(payload.Length, item.FileSize);
    }

    // ---- g) the remote changed -------------------------------------------------

    [Fact]
    public async Task A_Changed_ETag_Refuses_The_Resume_And_Restart_Refetches()
    {
        var original = Payload(4 * 1024 * 1024);
        var changed = Payload(4 * 1024 * 1024, seed: 7);

        await using var server = new TestHttpServer(original, chunkDelayMs: 25);
        var state = TempDir();
        var service = Engine(state);
        var destination = TempDir();

        var item = await service.StartDownloadAsync(server.Url, destination);
        await WaitAsync(() => item.ProgressBytes > 600_000, 30000, "the transfer to pass 600 KB");

        await service.PauseAsync(item.Id);
        Assert.True(item.Status == DownloadStatus.Paused,
            $"the transfer finished before the pause could land (status {item.Status})");
        Assert.Equal(1, StateFileCount(state));

        var beforeChange = item.ProgressBytes;

        // Same size, different bytes, different validator: the classic silent corruption.
        server.Payload = changed;
        server.ETag = "\"fixture-v2\"";

        await service.ResumeAsync(item.Id);
        await WaitAsync(item, DownloadStatus.Failed, 30000);

        Assert.Contains("ETag", item.ErrorMessage);
        Assert.Equal(beforeChange, item.ProgressBytes);
        Assert.False(File.Exists(item.DestinationPath), "the file must not be assembled from two versions");

        // The deliberate answer: throw the parts away and fetch the new object.
        await service.RestartAsync(item.Id);
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(changed), ShaFile(item.DestinationPath));
    }

    // ---- h) global limiter -----------------------------------------------------

    [Fact]
    public async Task The_Global_Limiter_Holds_The_Total_Rate_With_Eight_Connections()
    {
        const long limit = 512 * 1024;
        var payload = Payload(4 * 1024 * 1024);
        await using var server = new TestHttpServer(payload);
        var service = Engine(TempDir(), configure: options => options.GlobalSpeedLimitBps = limit);

        var watch = Stopwatch.StartNew();
        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);
        watch.Stop();

        var seconds = watch.Elapsed.TotalSeconds;
        var rate = payload.Length / seconds;

        _output.WriteLine(
            $"{payload.Length} bytes in {seconds:0.00}s = {rate / 1024:0} KiB/s " +
            $"against a {limit / 1024} KiB/s limit, {server.MaxConcurrentRequests} concurrent connections");

        Assert.True(server.MaxConcurrentRequests >= 4,
            $"only {server.MaxConcurrentRequests} connections: the limit was not shared by several");
        Assert.True(rate <= limit * 1.4,
            $"moved {rate / 1024:0} KiB/s against a limit of {limit / 1024} KiB/s");
        Assert.True(seconds >= 5.0, $"finished in {seconds:0.0}s, faster than the allowance permits");
        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    // ---- supporting behaviour ---------------------------------------------------

    [Fact]
    public async Task The_File_Name_Comes_From_Content_Disposition()
    {
        var payload = Payload(400_000);
        await using var server = new TestHttpServer(payload) { FileName = "report.csv" };
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.BaseUrl + "/download", TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal("report.csv", Path.GetFileName(item.DestinationPath));
        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    [Fact]
    public async Task Redirects_Are_Followed()
    {
        var payload = Payload(1_500_000);
        await using var server = new TestHttpServer(payload);
        server.RedirectTo = server.BaseUrl + "/redirected/payload.bin";

        var service = Engine(TempDir());
        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    [Fact]
    public async Task A_Transient_Failure_Is_Retried_With_Backoff()
    {
        var payload = Payload(1_200_000);
        await using var server = new TestHttpServer(payload) { FailuresBeforeSuccess = 2 };
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.True(item.RetryCount >= 1, "the retries were not counted");
        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    [Fact]
    public async Task Basic_Auth_Travels_With_Every_Request()
    {
        var payload = Payload(900_000);
        await using var server = new TestHttpServer(payload);
        var service = Engine(TempDir());

        var request = new HttpDownloadRequest(server.Url, TempDir())
        {
            UserName = "alice",
            Password = "s3cret"
        };

        var item = await service.StartAsync(request);
        await WaitAsync(item, DownloadStatus.Completed);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));
        Assert.NotEmpty(server.AuthorizationHeaders);
        Assert.All(server.AuthorizationHeaders, header => Assert.Equal(expected, header));
        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    [Fact]
    public async Task A_Manual_Proxy_Carries_The_Whole_Transfer()
    {
        var payload = Payload(2 * 1024 * 1024);
        await using var server = new TestHttpServer(payload);
        await using var proxy = new TestProxyServer();

        var service = Engine(TempDir());
        service.ApplyProxy(HttpProxyMode.Manual, "127.0.0.1", proxy.Port);

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
        Assert.Contains(proxy.Targets, target => target.Contains(server.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Empty_Object_Produces_An_Empty_File()
    {
        await using var server = new TestHttpServer(Array.Empty<byte>());
        var service = Engine(TempDir());

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.True(File.Exists(item.DestinationPath));
        Assert.Equal(0, new FileInfo(item.DestinationPath).Length);
        Assert.Equal(0, item.FileSize);
    }

    [Fact]
    public async Task A_Transfer_That_Lost_Its_Parts_Restarts_From_Zero()
    {
        var payload = Payload(2 * 1024 * 1024);
        // Slow enough that the pause always lands mid-flight: 2 MiB in 32 KiB chunks is ~1.6 s.
        await using var server = new TestHttpServer(payload, chunkDelayMs: 25);
        var state = TempDir();
        var service = Engine(state);

        var item = await service.StartDownloadAsync(server.Url, TempDir());
        await WaitAsync(() => item.ProgressBytes > 300_000, 30000, "the transfer to pass 300 KB");

        // PauseAsync settles the worker before returning, so the status below is the final word:
        // either Paused with a resume record, or Completed because the last segment was already in.
        await service.PauseAsync(item.Id);

        Assert.True(item.Status == DownloadStatus.Paused,
            $"the transfer finished before the pause could land (status {item.Status})");
        Assert.Equal(1, StateFileCount(state));

        // The user wiped the partial file behind the engine's back.
        File.Delete(item.DestinationPath + ".npart");

        await service.ResumeAsync(item.Id);
        await WaitAsync(item, DownloadStatus.Completed);

        Assert.Equal(Sha(payload), ShaFile(item.DestinationPath));
    }

    /// <summary>
    /// A real forwarding proxy over plain sockets: the client speaks absolute-URI to it, it speaks
    /// origin-form to the server. Small enough to trust, real enough to prove the proxy path.
    /// </summary>
    private sealed class TestProxyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly List<string> _targets = new();

        public TestProxyServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Targets
        {
            get { lock (_targets) return _targets.ToArray(); }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var downstream = client.GetStream();
                    var head = await ReadHeadAsync(downstream).ConfigureAwait(false);
                    if (head == null) return;

                    var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 3) return;

                    if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var target)) return;

                    lock (_targets) _targets.Add($"{parts[0]} {parts[1]}");

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(target.Host, target.Port, _cts.Token).ConfigureAwait(false);
                    var upstreamStream = upstream.GetStream();

                    var forwarded = new StringBuilder();
                    forwarded.Append($"{parts[0]} {target.PathAndQuery} {parts[2]}\r\n");
                    foreach (var line in lines.Skip(1)) forwarded.Append(line).Append("\r\n");
                    forwarded.Append("\r\n");

                    await upstreamStream
                        .WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()), _cts.Token)
                        .ConfigureAwait(false);

                    await upstreamStream.CopyToAsync(downstream, _cts.Token).ConfigureAwait(false);
                    await downstream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Fixture proxy: a broken connection ends the request, nothing else.
            }
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new byte[16384];
            var used = 0;

            while (used < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(used)).ConfigureAwait(false);
                if (read == 0) return null;
                used += read;

                var text = Encoding.ASCII.GetString(buffer, 0, used);
                if (text.Contains("\r\n\r\n")) return text;
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}
