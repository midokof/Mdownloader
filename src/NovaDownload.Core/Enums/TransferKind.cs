namespace NovaDownload.Core.Enums;

/// <summary>
/// Which engine owns a transfer. Chosen from the source string before anything is queued,
/// so <see cref="DownloadItem"/> carries its own routing decision.
/// </summary>
public enum TransferKind
{
    /// <summary>Direct http/https resource streamed by the HTTP engine.</summary>
    Http,

    /// <summary>BitTorrent swarm transfer: magnet link, local .torrent or a remote .torrent URL.</summary>
    Torrent
}
