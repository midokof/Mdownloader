using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Server;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// End-to-end over the real transport: a framed request goes into the per-user pipe exactly like
/// the native messaging host sends it, and the file that comes out was fetched by the download
/// engine — same queue, same data model as the desktop UI.
/// </summary>
public class BrowserBridgeIntegrationTests
{
    private sealed class Settings : ISettingsService
    {
        public Settings(string directory) => Current = new AppSettings { DefaultDownloadDirectory = directory };
        public AppSettings Current { get; }
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return data;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-bridge", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string UniquePipeName() => "NovaDownload.Test." + Guid.NewGuid().ToString("n");

    private static async Task<bool> WaitForCompletionAsync(IDownloadService service, Guid id, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var item = service.Snapshot().FirstOrDefault(entry => entry.Id == id);
            if (item is { Status: DownloadStatus.Completed }) return true;
            if (item is { Status: DownloadStatus.Failed })
                throw new Xunit.Sdk.XunitException($"Download failed: {item.ErrorMessage}");
            await Task.Delay(50);
        }

        return false;
    }

    private static async Task<BrowserBridgeServer> StartBridgeAsync(IDownloadService engine, string directory, string pipeName)
    {
        var processor = new BrowserRequestProcessor(engine, new Settings(directory));
        var bridge = new BrowserBridgeServer(processor, pipeName);
        bridge.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await BrowserBridgeProtocol.IsServerAvailableAsync(pipeName, TimeSpan.FromMilliseconds(300)))
                return bridge;
            await Task.Delay(100);
        }

        await bridge.DisposeAsync();
        throw new Xunit.Sdk.XunitException("the bridge pipe never came up");
    }

    [Fact]
    public async Task A_Browser_Requested_Media_Download_Is_Fetched_By_The_Engine()
    {
        var payload = Payload(600_000);
        await using var server = new TestHttpServer(payload) { FileName = "clip.mp4" };
        var directory = TempDir();
        var engine = new HttpDownloadService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, 2);
        var pipeName = UniquePipeName();

        await using var bridge = await StartBridgeAsync(engine, directory, pipeName);

        var request = new BrowserRequest
        {
            Action = BrowserActions.Download,
            Url = server.Url,
            FileName = "clip.mp4",
            PageUrl = "https://example.com/watch",
            MediaType = "video/mp4",
            Browser = "edge",
            RequestId = Guid.NewGuid().ToString("n")
        };

        var response = await BrowserBridgeProtocol.SendRequestAsync(request, pipeName);

        Assert.True(response.Ok, response.Message);
        Assert.Equal(ResponseCodes.Ok, response.Code);
        Assert.NotNull(response.DownloadId);
        Assert.Equal("clip.mp4", response.FileName);

        Assert.True(await WaitForCompletionAsync(engine, response.DownloadId!.Value), "the engine never finished the transfer");

        var item = engine.Snapshot().Single();
        Assert.Equal(server.Url, item.Url);
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    [Fact]
    public async Task A_Ping_Answers_Over_The_Pipe()
    {
        var engine = new HttpDownloadService(new HttpClient(), 1);
        var pipeName = UniquePipeName();
        await using var bridge = await StartBridgeAsync(engine, TempDir(), pipeName);

        var response = await BrowserBridgeProtocol.SendRequestAsync(new BrowserRequest { Action = BrowserActions.Ping }, pipeName);

        Assert.True(response.Ok);
        Assert.Equal(BrowserActions.ProtocolVersion, response.ProtocolVersion);
        Assert.Equal("novadownload-app", response.Server);
    }

    [Fact]
    public async Task Replaying_The_Same_Request_Does_Not_Queue_Twice()
    {
        var payload = Payload(200_000);
        await using var server = new TestHttpServer(payload, chunkDelayMs: 20) { FileName = "dup.mp4" };
        var engine = new HttpDownloadService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, 1);
        var pipeName = UniquePipeName();
        await using var bridge = await StartBridgeAsync(engine, TempDir(), pipeName);

        var request = new BrowserRequest
        {
            Action = BrowserActions.Download,
            Url = server.Url,
            FileName = "dup.mp4",
            RequestId = "replay-me"
        };

        var first = await BrowserBridgeProtocol.SendRequestAsync(request, pipeName);
        var second = await BrowserBridgeProtocol.SendRequestAsync(request, pipeName);

        Assert.True(first.Ok);
        Assert.Equal(ResponseCodes.Duplicate, second.Code);
        Assert.Single(engine.Snapshot());

        await WaitForCompletionAsync(engine, first.DownloadId!.Value);
    }

    [Fact]
    public async Task An_Unreachable_Application_Is_Reported_As_Unavailable()
    {
        var response = await BrowserBridgeProtocol.SendRequestAsync(
            new BrowserRequest { Action = BrowserActions.Ping },
            UniquePipeName(),
            TimeSpan.FromMilliseconds(300));

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.AppUnavailable, response.Code);
    }

    [Fact]
    public async Task A_Garbage_Frame_Gets_A_Malformed_Message_Answer()
    {
        var engine = new HttpDownloadEngineStub();
        var processor = new BrowserRequestProcessor(engine, new Settings(TempDir()));
        await using var bridge = new BrowserBridgeServer(processor, UniquePipeName());

        // 4-byte length behind a payload that is not JSON.
        var body = System.Text.Encoding.UTF8.GetBytes("not json at all");
        var framed = BitConverter.GetBytes(body.Length).Concat(body).ToArray();
        await using var stream = new MemoryStream();

        var response = await bridge.HandleConnectionAsync(new MemoryStream(framed), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.MalformedMessage, response.Code);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task A_Non_Http_Url_Never_Reaches_The_Engine()
    {
        var engine = new HttpDownloadEngineStub();
        var processor = new BrowserRequestProcessor(engine, new Settings(TempDir()));
        await using var bridge = new BrowserBridgeServer(processor, UniquePipeName());

        var payload = System.Text.Json.JsonSerializer.Serialize(new BrowserRequest
        {
            Action = BrowserActions.Download,
            Url = "file:///C:/Windows/win.ini"
        }, BrowserBridgeProtocol.Json);
        var framed = Frame(payload);

        var response = await bridge.HandleConnectionAsync(new MemoryStream(framed), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.UnsupportedScheme, response.Code);
        Assert.Empty(engine.Started);
    }

    /// <summary>
    /// A <c>magnet:</c> link clicked in Windows arrives with the torrent action (the media URL
    /// validator would refuse the scheme) and must reach the swarm engine untouched.
    /// </summary>
    [Fact]
    public async Task A_Magnet_Link_Reaches_The_Engine_Through_The_Torrent_Action()
    {
        const string magnet =
            "magnet:?xt=urn:btih:10553FF05A371CFDB0F25F9F0BD45AA937B442E8&dn=NovaDownload+sample";

        var engine = new HttpDownloadEngineStub();
        var settings = new Settings(TempDir());
        var processor = new BrowserRequestProcessor(engine, settings);
        await using var bridge = new BrowserBridgeServer(processor, UniquePipeName());

        var payload = System.Text.Json.JsonSerializer.Serialize(new BrowserRequest
        {
            Action = BrowserActions.Torrent,
            Url = magnet,
            Directory = settings.Current.DefaultDownloadDirectory
        }, BrowserBridgeProtocol.Json);

        var response = await bridge.HandleConnectionAsync(new MemoryStream(Frame(payload)), CancellationToken.None);

        Assert.True(response.Ok, $"{response.Code}: {response.Message}");
        var started = Assert.Single(engine.Started);
        Assert.Equal(magnet, started.Url);
        // The swarm names its own files once metadata arrives; a hint must not be forwarded.
        Assert.Null(started.FileName);
    }

    /// <summary>
    /// The magnet: handler is not browser integration: switching the extension off must not stop a
    /// torrent link from opening the application, while a browser media download stays refused.
    /// </summary>
    [Fact]
    public async Task Disabling_Browser_Integration_Does_Not_Stop_A_Magnet_Link()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";

        var engine = new HttpDownloadEngineStub();
        var settings = new Settings(TempDir());
        settings.Current.EnableBrowserIntegration = false;

        var processor = new BrowserRequestProcessor(engine, settings);
        await using var bridge = new BrowserBridgeServer(processor, UniquePipeName());

        var torrent = await bridge.HandleConnectionAsync(new MemoryStream(Frame(
            System.Text.Json.JsonSerializer.Serialize(new BrowserRequest
            {
                Action = BrowserActions.Torrent,
                Url = magnet
            }, BrowserBridgeProtocol.Json))), CancellationToken.None);

        var media = await bridge.HandleConnectionAsync(new MemoryStream(Frame(
            System.Text.Json.JsonSerializer.Serialize(new BrowserRequest
            {
                Action = BrowserActions.Download,
                Url = "https://example.com/clip.mp4"
            }, BrowserBridgeProtocol.Json))), CancellationToken.None);

        Assert.True(torrent.Ok, $"{torrent.Code}: {torrent.Message}");
        Assert.Equal(ResponseCodes.Disabled, media.Code);
        Assert.Single(engine.Started);
    }

    /// <summary>A magnet without an info hash cannot start a transfer, so it stops at the gate.</summary>
    [Fact]
    public async Task A_Magnet_Without_An_Info_Hash_Never_Reaches_The_Engine()
    {
        var engine = new HttpDownloadEngineStub();
        var processor = new BrowserRequestProcessor(engine, new Settings(TempDir()));
        await using var bridge = new BrowserBridgeServer(processor, UniquePipeName());

        var payload = System.Text.Json.JsonSerializer.Serialize(new BrowserRequest
        {
            Action = BrowserActions.Torrent,
            Url = "magnet:?dn=No+Hash+Here"
        }, BrowserBridgeProtocol.Json);

        var response = await bridge.HandleConnectionAsync(new MemoryStream(Frame(payload)), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.InvalidRequest, response.Code);
        Assert.Empty(engine.Started);
    }

    private static byte[] Frame(string payload)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(payload);
        return BitConverter.GetBytes(body.Length).Concat(body).ToArray();
    }

    /// <summary>Minimal engine used where the assertion is "nothing was started".</summary>
    private sealed class HttpDownloadEngineStub : IDownloadService
    {
        public List<(string Url, string Directory, string? FileName)> Started { get; } = new();

        public Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, string? fileName = null,
            CancellationToken cancellationToken = default)
        {
            Started.Add((url, destinationDirectory, fileName));
            var item = new DownloadItem { Url = url, FileName = fileName ?? "stub.bin" };
            return Task.FromResult(item);
        }

        public Task PauseAsync(Guid id) => Task.CompletedTask;
        public Task ResumeAsync(Guid id) => Task.CompletedTask;
        public Task CancelAsync(Guid id) => Task.CompletedTask;
        public Task RemoveAsync(Guid id) => Task.CompletedTask;
        public Task PauseAllAsync() => Task.CompletedTask;
        public Task ResumeAllAsync() => Task.CompletedTask;
        public IReadOnlyCollection<DownloadItem> Snapshot() => Array.Empty<DownloadItem>();

        public async IAsyncEnumerable<DownloadItem> ObserveDownloadsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
