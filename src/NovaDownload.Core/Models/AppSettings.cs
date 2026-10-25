namespace NovaDownload.Core.Models;

public class AppSettings
{
    public string DefaultDownloadDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int MaxConcurrentDownloads { get; set; } = 3;

    public int ConnectionsPerDownload { get; set; } = 4;

    public string Theme { get; set; } = "Light";

    /// <summary>"Auto" follows the Windows display language; "en" / "fr" pin one.</summary>
    public string Language { get; set; } = "Auto";

    public bool EnableBrowserIntegration { get; set; } = true;

    /// <summary>Restore and focus the window when a browser hands a download to NovaDownload.</summary>
    public bool ActivateOnBrowserDownload { get; set; } = true;

    public bool NotifyOnComplete { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    // ---- BitTorrent ------------------------------------------------------------

    /// <summary>TCP/UDP port the swarm listener binds. 0 asks the engine for an ephemeral port.</summary>
    public int TorrentListenPort { get; set; } = 51413;

    /// <summary>Distributed hash table lookup: needed for magnet links without a tracker.</summary>
    public bool TorrentEnableDht { get; set; } = true;

    /// <summary>Peer exchange between connected peers.</summary>
    public bool TorrentEnablePex { get; set; } = true;

    /// <summary>Local service discovery: find peers on the same LAN.</summary>
    public bool TorrentEnableLsd { get; set; } = true;

    /// <summary>Ask the router to open the listen port (UPnP / NAT-PMP). Off means no UPnP traffic.</summary>
    public bool TorrentEnablePortForwarding { get; set; } = true;

    /// <summary>Keep offering completed pieces to the swarm.</summary>
    public bool TorrentSeedAfterCompletion { get; set; } = true;

    /// <summary>Global upload ceiling in KiB/s. 0 means unlimited.</summary>
    public int TorrentMaxUploadKib { get; set; }

    /// <summary>Global download ceiling in KiB/s. 0 means unlimited.</summary>
    public int TorrentMaxDownloadKib { get; set; }

    /// <summary>Connections per torrent.</summary>
    public int TorrentConnectionsPerTorrent { get; set; } = 60;

    /// <summary>Total connections across every torrent.</summary>
    public int TorrentMaxConnections { get; set; } = 200;

    /// <summary>
    /// Register <c>magnet:</c> for this user so a magnet click opens NovaDownload. Windows keeps a
    /// single handler per scheme, so turning this on takes magnet links from other BitTorrent
    /// clients; turning it off gives the scheme back.
    /// </summary>
    public bool TorrentHandleMagnetLinks { get; set; } = true;
}
