namespace NovaDownload.Core.Enums;

/// <summary>
/// The categories the left pane filters by. They are derived from the file name (and the transfer
/// kind), so a row knows where it belongs the moment it is created — no folder to scan.
/// </summary>
public enum DownloadCategory
{
    /// <summary>No rule matched the extension: the row still appears under "Tous".</summary>
    Other,

    Compressed,
    Documents,
    Music,
    Programs,
    Video,

    /// <summary>Every BitTorrent transfer, whatever the payload extension.</summary>
    Torrents
}

/// <summary>What the category pane currently selects.</summary>
public enum DownloadScope
{
    All,
    Category,
    Custom,
    Unfinished,
    Finished,
    Queue
}
