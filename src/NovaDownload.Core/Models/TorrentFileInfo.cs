namespace NovaDownload.Core.Models;

/// <summary>
/// One file inside a multi-file torrent. This is the plain data model; the view model
/// (<see cref="NovaDownload.App.ViewModels.TorrentFileViewModel"/>) wraps it for the UI.
/// </summary>
public sealed class TorrentFileInfo
{
    public TorrentFileInfo(string path, long length, bool isSelected = true)
    {
        Path = path;
        Length = length;
        IsSelected = isSelected;
    }

    /// <summary>Relative path inside the torrent (e.g. "folder/episode01.mkv").</summary>
    public string Path { get; }

    /// <summary>File size in bytes.</summary>
    public long Length { get; }

    /// <summary>User-facing name (last path segment).</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Whether this file will be downloaded.</summary>
    public bool IsSelected { get; set; }
}