namespace NovaDownload.DownloadEngine.Torrenting;

/// <summary>
/// Mutable snapshot of the BitTorrent preferences. <see cref="TorrentDownloadService"/> reads the
/// fields when it builds the session and when it applies a settings change, so nothing here is
/// captured at construction time.
/// </summary>
public sealed class TorrentEngineOptions
{
    /// <summary>Where the engine keeps DHT nodes, magnet metadata and fast-resume data.</summary>
    public string CacheDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaDownload", "torrent-cache");

    public int ListenPort { get; set; } = 51413;

    public bool EnableDht { get; set; } = true;

    public bool EnablePeerExchange { get; set; } = true;

    public bool EnableLocalPeerDiscovery { get; set; } = true;

    public bool EnablePortForwarding { get; set; } = true;

    public bool SeedAfterCompletion { get; set; } = true;

    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxUploadKib { get; set; }

    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxDownloadKib { get; set; }

    public int ConnectionsPerTorrent { get; set; } = 60;

    public int MaxConnections { get; set; } = 200;

    public int UploadSlots { get; set; } = 4;

    /// <summary>Rejects torrent files above this size: a .torrent is metadata, not a payload.</summary>
    public long MaxTorrentFileBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>How long a magnet link may take to fetch its metadata before the transfer fails.</summary>
    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Folder holding one JSON resume file per torrent.</summary>
    public string StateDirectory { get; set; } = DefaultStateDirectory();

    /// <summary>
    /// <c>%LOCALAPPDATA%\NovaDownload\torrent-state</c>, or the folder named by
    /// <c>NOVADOWNLOAD_DATA_DIR</c> when the environment forces one (tests, portable runs).
    /// </summary>
    public static string DefaultStateDirectory()
    {
        var root = Environment.GetEnvironmentVariable("NOVADOWNLOAD_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaDownload");

        return Path.Combine(root, "torrent-state");
    }

    public static long Limit(int kib) => kib <= 0 ? 0 : kib * 1024L;
}
