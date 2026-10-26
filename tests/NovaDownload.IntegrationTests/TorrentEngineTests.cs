using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using NovaDownload.Core.Enums;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// Real BitTorrent traffic over the loopback interface: a torrent is built from a temporary
/// payload, one side seeds it through <see cref="TorrentDownloadService"/> or a bare
/// <see cref="ClientEngine"/>, the other side downloads it. No tracker, no DHT, no internet.
/// </summary>
public class TorrentEngineTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Binds port 0, reads the port the OS handed out, releases it.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(string TorrentPath, byte[] Payload, string SeedDir)> BuildTorrentAsync(int size)
    {
        var root = TempDir();
        var seedDir = Path.Combine(root, "seed");
        Directory.CreateDirectory(seedDir);

        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        var payloadPath = Path.Combine(seedDir, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);

        var torrentPath = Path.Combine(root, "payload.torrent");
        await new TorrentCreator { Comment = "NovaDownload loopback test" }
            .CreateAsync(new TorrentFileSource(payloadPath), torrentPath);

        return (torrentPath, payload, seedDir);
    }

    private static TorrentEngineOptions Options(int port, string cache) => new()
    {
        ListenPort = port,
        CacheDirectory = cache,
        // A test run must never touch the resume files of the real profile.
        StateDirectory = TempDir(),
        EnableDht = false,
        EnablePeerExchange = false,
        EnableLocalPeerDiscovery = false,
        EnablePortForwarding = false,
        SeedAfterCompletion = true,
        MaxConnections = 40,
        ConnectionsPerTorrent = 20,
        MetadataTimeout = TimeSpan.FromSeconds(20)
    };

    private static ClientEngine BareEngine(int port, string cache) => new(new EngineSettingsBuilder
    {
        AllowPortForwarding = false,
        AllowLocalPeerDiscovery = false,
        AutoSaveLoadDhtCache = false,
        AutoSaveLoadFastResume = false,
        CacheDirectory = cache,
        MaximumConnections = 40,
        ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, port) },
        DhtEndPoint = new IPEndPoint(IPAddress.Any, port),
    }.ToSettings());

    private static TorrentSettings BareSettings(bool initialSeeding) => new TorrentSettingsBuilder
    {
        AllowDht = false,
        AllowPeerExchange = false,
        AllowInitialSeeding = initialSeeding,
        CreateContainingDirectory = false,
        MaximumConnections = 20,
    }.ToSettings();

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out after {timeoutMs} ms waiting for {what}.");
    }

    [Fact]
    public async Task Torrent_File_Source_Is_Detected_And_Seeds_Verified_Data()
    {
        var (torrentPath, payload, seedDir) = await BuildTorrentAsync(1024 * 1024);
        var cache = TempDir();

        Assert.True(TorrentSource.IsTorrent(torrentPath), "a local .torrent path must be recognised");
        Assert.False(TorrentSource.IsTorrent("https://example.com/movie.mp4"));
        Assert.True(TorrentSource.IsTorrent("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"));
        Assert.True(TorrentSource.IsTorrent("https://example.com/file.torrent"));

        await using var service = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(FreePort(), cache));

        // Destination is the folder that already holds the data: the service hashes it and seeds.
        var item = await service.StartDownloadAsync(torrentPath, seedDir);
        Assert.Equal(TransferKind.Torrent, item.Kind);

        await WaitAsync(() => item.Status == DownloadStatus.Completed, 30_000, $"completion (status {item.Status}, {item.ErrorMessage})");

        Assert.Equal(payload.Length, item.FileSize);
        Assert.Equal(item.FileSize, item.ProgressBytes);
        Assert.Equal("payload.bin", item.FileName);
        Assert.Equal(Path.Combine(seedDir, "payload.bin"), item.DestinationPath);
        Assert.False(string.IsNullOrEmpty(item.InfoHash));
        Assert.Equal(40, item.InfoHash!.Length); // v1 infohash, hex
        Assert.Equal(100, item.ProgressPercent, 3);
    }

    [Fact]
    public async Task Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes()
    {
        var (torrentPath, payload, seedDir) = await BuildTorrentAsync(2 * 1024 * 1024);
        var seederPort = FreePort();
        var leecherPort = FreePort();
        var leecherDir = TempDir();

        await using var seeder = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(seederPort, TempDir()));

        var seeded = await seeder.StartDownloadAsync(torrentPath, seedDir);
        await WaitAsync(() => seeded.Status == DownloadStatus.Completed, 30_000,
            $"the service to finish hashing and seed (status {seeded.Status}, {seeded.ErrorMessage})");

        // A bare engine on the other side: this proves the service really listens and serves.
        var torrent = Torrent.Load(torrentPath);
        using var client = BareEngine(leecherPort, TempDir());
        var manager = await client.AddAsync(torrent, leecherDir, BareSettings(initialSeeding: false));
        await manager.StartAsync();

        Assert.True(await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seederPort}"))),
            "the seeder refused the loopback peer");

        // A connected peer must never read as "0 seeds · 0 peers": the engine's own peer manager only
        // counts finished handshakes, so the row derives the pair count from live connections.
        var sawLiveConnection = false;
        var probeUntil = DateTime.UtcNow.AddSeconds(20);
        while (!sawLiveConnection && DateTime.UtcNow < probeUntil)
        {
            if (seeded.ConnectedPeers > 0)
            {
                Assert.True(seeded.Seeds + seeded.Leechers >= 1,
                    $"connected={seeded.ConnectedPeers} but the row reads {seeded.Seeds} seeds · {seeded.Leechers} peers");
                sawLiveConnection = true;
            }
            await Task.Delay(50);
        }
        Assert.True(sawLiveConnection, "the seeding row never reported the leecher connecting");

        await WaitAsync(() => manager.Complete, 60_000,
            $"the swarm transfer to complete (state {manager.State}, progress {manager.Progress:0.00})");

        var written = Path.Combine(leecherDir, "payload.bin");
        Assert.True(File.Exists(written), "the transfer finished without writing the file");
        Assert.Equal(payload, await File.ReadAllBytesAsync(written));

        // The seeding side must report live upload telemetry for the completed row.
        await WaitAsync(() => seeded.UploadedBytes > 0, 20_000, "upload telemetry on the seeding row");
        Assert.True(seeded.Ratio > 0, $"ratio should be positive once data has been served, got {seeded.Ratio}");

        await client.StopAllAsync();
    }

    [Fact]
    public async Task A_Partial_Download_Never_Reports_Completed()
    {
        const int size = 2 * 1024 * 1024;
        var (torrentPath, payload, _) = await BuildTorrentAsync(size);

        // A quarter of the payload is already on disk. The engine verifies those pieces and must
        // project roughly 25% — MonoTorrent 3.0.2 reports Progress on a 0..100 scale, and reading it
        // as 0..1 made any progress past one percent render as a finished row.
        var workDir = TempDir();
        var partial = new byte[size / 4];
        Array.Copy(payload, partial, partial.Length);
        await File.WriteAllBytesAsync(Path.Combine(workDir, "payload.bin"), partial);

        await using var service = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(FreePort(), TempDir()));

        var item = await service.StartDownloadAsync(torrentPath, workDir);

        await WaitAsync(() => item.Phase == TorrentState.Downloading.ToString(), 30_000,
            $"the hash check to settle (phase {item.Phase}, status {item.Status})");
        await Task.Delay(1000);

        Assert.NotEqual(DownloadStatus.Completed, item.Status);
        Assert.True(item.ProgressPercent > 5, $"progress collapsed to {item.ProgressPercent:0.0}%");
        Assert.True(item.ProgressPercent < 95,
            $"a quarter of the payload reported as {item.ProgressPercent:0.0}% complete");
        Assert.True(item.ProgressBytes < item.FileSize, "the row claimed a full byte count");
    }

    [Fact]
    public async Task Cancel_Removes_The_Partial_Data_And_Stops_The_Row()
    {
        var (torrentPath, _, seedDir) = await BuildTorrentAsync(512 * 1024);
        var cache = TempDir();
        var workDir = TempDir();
        var port = FreePort();

        await using var service = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(port, cache));

        var item = await service.StartDownloadAsync(torrentPath, workDir);
        // Wait until the manager exists and has been started, then cancel while it is hashing.
        await WaitAsync(() => item.Status is DownloadStatus.Downloading or DownloadStatus.Completed, 30_000,
            $"the transfer to start (status {item.Status})");

        await service.CancelAsync(item.Id);

        Assert.Equal(DownloadStatus.Canceled, item.Status);
        Assert.Empty(service.Snapshot());
        Assert.False(File.Exists(Path.Combine(workDir, "payload.bin")),
            "cancel must not leave the downloaded file behind");
    }

    [Fact]
    public async Task A_Repeat_Source_Returns_The_Live_Transfer_Instead_Of_Failing()
    {
        var (torrentPath, _, seedDir) = await BuildTorrentAsync(256 * 1024);

        await using var service = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(FreePort(), TempDir()));

        var first = await service.StartDownloadAsync(torrentPath, seedDir);
        var second = await service.StartDownloadAsync(torrentPath, seedDir);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.Snapshot());
    }

    [Fact]
    public async Task Status_And_Control_Round_Trip_Through_The_Seeding_Row()
    {
        var (torrentPath, _, seedDir) = await BuildTorrentAsync(256 * 1024);

        await using var service = new TorrentDownloadService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options(FreePort(), TempDir()));

        var item = await service.StartDownloadAsync(torrentPath, seedDir);
        await WaitAsync(() => item.Status == DownloadStatus.Completed, 30_000,
            $"completion (status {item.Status}, {item.ErrorMessage})");

        await service.PauseAsync(item.Id);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        await service.ResumeAsync(item.Id);
        Assert.NotEqual(DownloadStatus.Paused, item.Status);

        await service.RemoveAsync(item.Id);
        Assert.Empty(service.Snapshot());
        Assert.True(File.Exists(Path.Combine(seedDir, "payload.bin")),
            "removing a finished row must keep the data on disk");
    }
}
