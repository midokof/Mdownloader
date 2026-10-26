namespace NovaDownload.BrowserIntegration.Protocol;

/// <summary>Actions a browser extension may ask the desktop application to perform.</summary>
public static class BrowserActions
{
    /// <summary>Handshake: is NovaDownload running, which version, which default folder.</summary>
    public const string Ping = "ping";

    /// <summary>Hand a validated direct-media URL to the existing download engine.</summary>
    public const string Download = "download";

    /// <summary>
    /// Hand a magnet link, a .torrent path or a .torrent URL to the BitTorrent engine. Used by the
    /// <c>magnet:</c> protocol handler and by the command line, not by the extension.
    /// </summary>
    public const string Torrent = "torrent";

    /// <summary>Bring the application window to the front without starting a transfer.</summary>
    public const string Open = "open";

    /// <summary>Queue counters only.</summary>
    public const string Status = "status";

    /// <summary>Wire version understood by this build.</summary>
    public const int ProtocolVersion = 1;

    public static bool IsKnown(string? action) =>
        action is Ping or Download or Open or Status or Torrent;
}
