using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent;
using MonoTorrent.Client;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// Step 3 contract: a .torrent is opened and lists its files, only the selected files are fetched,
/// a magnet is accepted, a paused swarm resumes after the process is gone, the row can be dropped
/// with or without its data, both engines share one list, and the rate limit really holds.
/// The swarm is real but local: a bare engine seeds, the service downloads, no tracker, no DHT.
/// </summary>
public class TorrentConsolidationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-torrent", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Three files in one folder, so selection has something to choose between.</summary>
    private static async Task<(string TorrentPath, string SeedRoot, Dictionary<string, byte[]> Files)>
        BuildFolderTorrentAsync()
    {
        var root = TempDir();
        var seedRoot = Path.Combine(root, "seed");
        var folder = Path.Combine(seedRoot, "payload");
        Directory.CreateDirectory(folder);

        var files = new Dictionary<string, byte[]>();
        foreach (var (name, size) in new[]
                 {
                     ("keep.bin", 300_000),
                     ("skip-a.bin", 200_000),
                     ("skip-b.bin", 150_000)
                 })
        {
            var data = new byte[size];
            new Random(size + name.Length).NextBytes(data);
            await File.WriteAllBytesAsync(Path.Combine(folder, name), data);
            files[name] = data;
        }

        var torrentPath = Path.Combine(root, "payload.torrent");
        await new TorrentCreator { Comment = "NovaDownload selection test" }
            .CreateAsync(new TorrentFileSource(folder), torrentPath);

        return (torrentPath, seedRoot, files);
    }

    private static async Task<(string TorrentPath, byte[] Payload, string SeedDir)>
        BuildSingleFileTorrentAsync(int size)
    {
        var root = TempDir();
        var seedDir = Path.Combine(root, "seed");
        Directory.CreateDirectory(seedDir);

        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(seedDir, "payload.bin"), payload);

        var torrentPath = Path.Combine(root, "payload.torrent");
        await new TorrentCreator { Comment = "NovaDownload loopback test" }
            .CreateAsync(new TorrentFileSource(Path.Combine(seedDir, "payload.bin")), torrentPath);

        return (torrentPath, payload, seedDir);
    }

    private static TorrentEngineOptions Options(int port, string cache, string state) => new()
    {
        ListenPort = port,
        CacheDirectory = cache,
        StateDirectory = state,
        EnableDht = false,
        EnablePeerExchange = false,
        EnableLocalPeerDiscovery = false,
        EnablePortForwarding = false,
        SeedAfterCompletion = false,
        MaxConnections = 40,
        ConnectionsPerTorrent = 20,
        MetadataTimeout = TimeSpan.FromSeconds(20)
    };

    private static TorrentDownloadService Service(int port, string cache, string state,
        Action<TorrentEngineOptions>? configure = null)
    {
        var options = Options(port, cache, state);
        configure?.Invoke(options);
        return new TorrentDownloadService(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options);
    }

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

    private static int StateCount(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;

    /// <summary>
    /// A bare MonoTorrent engine holding verified data and dialling out. It is the peer the service
    /// cannot find by itself: these torrents carry no tracker and DHT is off in the tests.
    /// </summary>
    private sealed class Seeder : IAsyncDisposable
    {
        private readonly ClientEngine _engine;
        private readonly TorrentManager _manager;

        private Seeder(ClientEngine engine, TorrentManager manager, int port)
        {
            _engine = engine;
            _manager = manager;
            Port = port;
        }

        public int Port { get; }

        public static async Task<Seeder> StartAsync(string torrentPath, string dataRoot)
        {
            var port = FreePort();

            var engine = new ClientEngine(new EngineSettingsBuilder
            {
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                CacheDirectory = TempDir(),
                MaximumConnections = 40,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, port) },
                DhtEndPoint = new IPEndPoint(IPAddress.Any, port),
            }.ToSettings());

            var torrent = Torrent.Load(torrentPath);
            var manager = await engine.AddAsync(torrent, dataRoot, new TorrentSettingsBuilder
            {
                AllowDht = false,
                AllowPeerExchange = false,
                AllowInitialSeeding = true,
                // MonoTorrent keeps the torrent name out of File.Path and adds it back through the
                // containing directory, so a folder torrent only verifies with this on.
                CreateContainingDirectory = true,
                MaximumConnections = 20,
            }.ToSettings());

            await manager.StartAsync();
            await WaitAsync(() => manager.State == TorrentState.Seeding, 60_000,
                $"the seeder to verify its data (state {manager.State}, {manager.Progress:0.0}% verified)");

            return new Seeder(engine, manager, port);
        }

        /// <summary>The seeder is the one dialling: the service's peer discovery is deliberately off.</summary>
        public Task<bool> ConnectAsync(int leecherPort) =>
            _manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{leecherPort}")));

        public async ValueTask DisposeAsync()
        {
            try { await _engine.StopAllAsync(); } catch { }
            try { _engine.Dispose(); } catch { }
        }
    }

    // ---- b) a .torrent is opened and its files listed --------------------------

    [Fact]
    public async Task A_Torrent_Lists_Every_File_With_Its_Size()
    {
        var (torrentPath, _, files) = await BuildFolderTorrentAsync();

        await using var service = Service(FreePort(), TempDir(), TempDir());
        var listed = await service.LoadTorrentMetadataAsync(torrentPath);

        Assert.Equal(3, listed.Count);
        foreach (var file in listed)
        {
            var name = Path.GetFileName(file.Path);
            Assert.True(files.ContainsKey(name), $"unexpected file in the listing: {file.Path}");
            Assert.Equal(files[name].Length, file.Length);
            Assert.True(file.IsSelected, "every file starts selected");
        }

        Assert.Contains(listed, f => f.Path.EndsWith("keep.bin", StringComparison.Ordinal));
    }

    // ---- d) only the selected files are downloaded -----------------------------

    [Fact]
    public async Task Only_The_Selected_Files_Land_On_Disk_With_The_Right_Bytes()
    {
        var (torrentPath, seedRoot, files) = await BuildFolderTorrentAsync();
        var leecherPort = FreePort();
        var leecherDir = TempDir();
        var state = TempDir();

        await using var seeder = await Seeder.StartAsync(torrentPath, seedRoot);
        await using var service = Service(leecherPort, TempDir(), state);

        var listed = await service.LoadTorrentMetadataAsync(torrentPath);
        var selection = listed
            .Select(f => new TorrentFileInfo(f.Path, f.Length, Path.GetFileName(f.Path) == "keep.bin"))
            .ToArray();

        var item = await service.StartDownloadAsync(torrentPath, leecherDir, selection);
        Assert.True(await seeder.ConnectAsync(leecherPort), "the seeder refused the leecher");

        await WaitAsync(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            120_000, $"the selected file to arrive (status {item.Status}, {item.ErrorMessage}, {item.Phase})");

        Assert.Equal(DownloadStatus.Completed, item.Status);

        var kept = Path.Combine(leecherDir, "payload", "keep.bin");
        Assert.True(File.Exists(kept), "the selected file is missing");
        Assert.Equal(Sha(files["keep.bin"]), Sha(await File.ReadAllBytesAsync(kept)));

        var unexpected = new List<string>();
        foreach (var skipped in new[] { "skip-a.bin", "skip-b.bin" })
        {
            var path = Path.Combine(leecherDir, "payload", skipped);
            if (File.Exists(path)) unexpected.Add($"{skipped} ({new FileInfo(path).Length} bytes)");
        }

        Assert.Empty(unexpected);

        // A finished transfer leaves nothing to resume.
        Assert.Equal(0, StateCount(state));
        Assert.Equal(100, item.ProgressPercent, 1);
    }

    // ---- c) a magnet is accepted ----------------------------------------------

    [Fact]
    public async Task A_Magnet_Link_Is_Accepted_And_Exposes_Its_Info_Hash()
    {
        var (torrentPath, _, _) = await BuildFolderTorrentAsync();
        var torrent = Torrent.Load(torrentPath);
        var hash = torrent.InfoHashes.V1OrV2!.ToHex();
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=payload";

        await using var service = Service(FreePort(), TempDir(), TempDir());
        var item = await service.StartDownloadAsync(magnet, TempDir());

        await WaitAsync(() => item.InfoHash != null || item.Status == DownloadStatus.Failed,
            30_000, $"the magnet to be parsed (status {item.Status}, {item.ErrorMessage})");

        Assert.Equal(DownloadStatus.Downloading, item.Status);
        Assert.Equal(TransferKind.Torrent, item.Kind);
        Assert.Equal(magnet, item.Url);
        Assert.Equal(hash, item.InfoHash);
        Assert.Single(service.Snapshot());

        await service.CancelAsync(item.Id);
        Assert.Empty(service.Snapshot());
    }

    // ---- e) pause, restart the process, resume --------------------------------

    [Fact]
    public async Task A_Restarted_Service_Resumes_A_Paused_Torrent_To_The_Exact_Bytes()
    {
        var (torrentPath, payload, seedRoot) = await BuildSingleFileTorrentAsync(4 * 1024 * 1024);
        var leecherDir = TempDir();
        var state = TempDir();

        await using var seeder = await Seeder.StartAsync(torrentPath, seedRoot);

        var firstPort = FreePort();
        // Throttled on purpose: 4 MiB would otherwise finish on loopback before the pause lands.
        var first = Service(firstPort, TempDir(), state, options => options.MaxDownloadKib = 128);
        var item = await first.StartDownloadAsync(torrentPath, leecherDir);
        Assert.True(await seeder.ConnectAsync(firstPort), "the seeder refused the first leecher");

        await WaitAsync(() => item.ProgressBytes > 1_500_000, 120_000,
            $"the transfer to pass 1.5 MB (progress {item.ProgressBytes}, phase {item.Phase})");

        await first.PauseAsync(item.Id);
        await WaitAsync(() => item.Status == DownloadStatus.Paused, 20_000, "the row to pause");
        await WaitAsync(() => StateCount(state) == 1, 20_000, "the JSON state file to appear");

        var reached = item.ProgressBytes;
        Assert.True(reached > 0 && reached < payload.Length, $"paused at {reached} of {payload.Length}");

        // The process "dies": the engine goes away, only the folder and the JSON survive.
        await first.DisposeAsync();

        var secondPort = FreePort();
        await using var second = Service(secondPort, TempDir(), state);
        Assert.Equal(1, second.Restore());

        var restored = Assert.Single(second.Snapshot());
        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.Equal(item.Url, restored.Url);
        Assert.Equal(leecherDir, Path.GetDirectoryName(restored.DestinationPath), ignoreCase: true);
        Assert.Equal("payload.bin", Path.GetFileName(restored.DestinationPath));

        Assert.True(await seeder.ConnectAsync(secondPort), "the seeder refused the restored leecher");
        await second.ResumeAsync(restored.Id);

        await WaitAsync(() => restored.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            120_000, $"the restart to finish (status {restored.Status}, {restored.ErrorMessage}, {restored.Phase})");

        Assert.Equal(DownloadStatus.Completed, restored.Status);
        Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(Path.Combine(leecherDir, "payload.bin"))));
        Assert.Equal(0, StateCount(state));
    }

    // ---- deletion with or without the data -------------------------------------

    [Fact]
    public async Task Removing_A_Torrent_Can_Keep_Or_Delete_Its_Data()
    {
        var (torrentPath, payload, seedDir) = await BuildSingleFileTorrentAsync(512 * 1024);

        await using var service = Service(FreePort(), TempDir(), TempDir());

        // Seeding a folder that already holds the data: the row completes without a swarm.
        var kept = await service.StartDownloadAsync(torrentPath, seedDir);
        await WaitAsync(() => kept.Status == DownloadStatus.Completed, 60_000,
            $"the row to complete (status {kept.Status}, {kept.ErrorMessage})");

        await service.RemoveAsync(kept.Id, deleteFiles: false);
        Assert.Empty(service.Snapshot());
        Assert.True(File.Exists(Path.Combine(seedDir, "payload.bin")),
            "removing without deleting must leave the payload");

        var deleted = await service.StartDownloadAsync(torrentPath, seedDir);
        await WaitAsync(() => deleted.Status == DownloadStatus.Completed, 60_000,
            $"the second row to complete (status {deleted.Status}, {deleted.ErrorMessage})");

        await service.RemoveAsync(deleted.Id, deleteFiles: true);
        Assert.Empty(service.Snapshot());
        Assert.False(File.Exists(Path.Combine(seedDir, "payload.bin")),
            "removing with deletion must remove the payload");

        Assert.True(payload.Length > 0);
    }

    // ---- f) HTTP and torrent share one list -----------------------------------

    [Fact]
    public async Task Http_And_Torrent_Coexist_In_The_Same_List()
    {
        var httpPayload = new byte[700_000];
        new Random(11).NextBytes(httpPayload);
        await using var server = new TestHttpServer(httpPayload);

        var (torrentPath, torrentPayload, seedDir) = await BuildSingleFileTorrentAsync(512 * 1024);

        var http = new HttpDownloadService(
            new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, 3,
            new NovaDownload.DownloadEngine.Http.HttpEngineOptions { StateDirectory = TempDir() });

        await using var torrent = Service(FreePort(), TempDir(), TempDir());
        var router = new DownloadServiceRouter(http, torrent);

        var httpItem = await router.StartDownloadAsync(server.Url, TempDir());
        var torrentItem = await router.StartDownloadAsync(torrentPath, seedDir);

        await WaitAsync(
            () => httpItem.Status is DownloadStatus.Completed or DownloadStatus.Failed &&
                  torrentItem.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            90_000, $"both transfers to settle (http {httpItem.Status}, torrent {torrentItem.Status})");

        Assert.Equal(DownloadStatus.Completed, httpItem.Status);
        Assert.Equal(DownloadStatus.Completed, torrentItem.Status);
        Assert.Equal(TransferKind.Http, httpItem.Kind);
        Assert.Equal(TransferKind.Torrent, torrentItem.Kind);

        var list = router.Snapshot();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.Kind == TransferKind.Http);
        Assert.Contains(list, i => i.Kind == TransferKind.Torrent);

        Assert.Equal(Sha(httpPayload), Sha(await File.ReadAllBytesAsync(httpItem.DestinationPath)));
        Assert.Equal(Sha(torrentPayload), Sha(await File.ReadAllBytesAsync(torrentItem.DestinationPath)));

        // The torrent row must carry swarm telemetry, not HTTP-shaped numbers.
        Assert.False(string.IsNullOrEmpty(torrentItem.InfoHash));
        Assert.Equal("BT", torrentItem.Extension);
        Assert.Equal(0, httpItem.Ratio);
    }

    // ---- configurable rate limit ----------------------------------------------

    [Fact]
    public async Task The_Torrent_Rate_Limit_Holds_The_Swarm_Download()
    {
        const int limitKib = 256;
        var (torrentPath, payload, seedRoot) = await BuildSingleFileTorrentAsync(1024 * 1024);
        var leecherPort = FreePort();

        await using var seeder = await Seeder.StartAsync(torrentPath, seedRoot);
        await using var service = Service(leecherPort, TempDir(), TempDir(),
            options => options.MaxDownloadKib = limitKib);

        var watch = Stopwatch.StartNew();
        var item = await service.StartDownloadAsync(torrentPath, TempDir());
        Assert.True(await seeder.ConnectAsync(leecherPort), "the seeder refused the leecher");

        await WaitAsync(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            180_000, $"the throttled transfer to finish (status {item.Status}, {item.ErrorMessage})");
        watch.Stop();

        if (item.Status != DownloadStatus.Completed)
            throw new Xunit.Sdk.XunitException($"throttled transfer failed: {item.ErrorMessage}");

        var seconds = watch.Elapsed.TotalSeconds;
        var rate = payload.Length / seconds;

        // 1 MiB at 256 KiB/s cannot be done in under four seconds even on loopback.
        Assert.True(seconds >= 3.0,
            $"moved {rate / 1024:0} KiB/s in {seconds:0.00}s against a {limitKib} KiB/s limit");
        Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(item.DestinationPath)));
    }
}
