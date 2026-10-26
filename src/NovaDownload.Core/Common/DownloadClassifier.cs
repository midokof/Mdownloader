using NovaDownload.Core.Enums;
using NovaDownload.Core.Localization;

namespace NovaDownload.Core.Common;

/// <summary>
/// Extension table behind the "Type" column and the category pane. One table, one answer: the
/// confirmation dialog, the row and the filter cannot disagree about where a file belongs.
/// </summary>
public static class DownloadClassifier
{
    private static readonly Dictionary<string, DownloadCategory> ByExtension = Build();
    private static readonly Dictionary<DownloadCategory, string[]> ByCategory = BuildReverse();

    /// <summary>Category of a transfer, from its file name and its kind.</summary>
    public static DownloadCategory Classify(string? fileName, bool isTorrent)
    {
        if (isTorrent) return DownloadCategory.Torrents;
        if (string.IsNullOrWhiteSpace(fileName)) return DownloadCategory.Other;

        var extension = System.IO.Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || extension.Length < 2) return DownloadCategory.Other;

        return ByExtension.TryGetValue(extension[1..].ToLowerInvariant(), out var category)
            ? category
            : DownloadCategory.Other;
    }

    /// <summary>Localized label for the Type column and the pane.</summary>
    public static string Text(DownloadCategory category) => Localizer.Get(Key(category));

    /// <summary>Catalog key of a category label, so a view can bind it through <c>{loc:Tr}</c>.</summary>
    public static string Key(DownloadCategory category) => category switch
    {
        DownloadCategory.Compressed => "Cat_Compressed",
        DownloadCategory.Documents => "Cat_Documents",
        DownloadCategory.Music => "Cat_Music",
        DownloadCategory.Programs => "Cat_Programs",
        DownloadCategory.Video => "Cat_Video",
        DownloadCategory.Torrents => "Cat_Torrents",
        _ => "Cat_Other"
    };

    /// <summary>Extensions the table files under <paramref name="category"/>, for the Options tab.</summary>
    public static IReadOnlyList<string> Extensions(DownloadCategory category) =>
        ByCategory.TryGetValue(category, out var extensions) ? extensions : Array.Empty<string>();

    private static Dictionary<string, DownloadCategory> Build()
    {
        var map = new Dictionary<string, DownloadCategory>(StringComparer.Ordinal);

        Add(map, DownloadCategory.Compressed,
            "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "cab", "lz", "lzma", "zst");

        Add(map, DownloadCategory.Documents,
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt",
            "csv", "epub", "mobi", "md", "tex", "djvu");

        Add(map, DownloadCategory.Music,
            "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "wma", "opus", "aiff", "aif", "mid", "midi");

        Add(map, DownloadCategory.Programs,
            "exe", "msi", "msix", "appx", "msu", "bat", "cmd", "com", "apk", "appimage", "deb", "rpm",
            "dmg", "pkg", "jar", "vbs", "ps1", "iso", "img");

        Add(map, DownloadCategory.Video,
            "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "3gp", "ogv", "m2ts");

        return map;
    }

    private static void Add(Dictionary<string, DownloadCategory> map, DownloadCategory category, params string[] extensions)
    {
        foreach (var extension in extensions) map[extension] = category;
    }

    private static Dictionary<DownloadCategory, string[]> BuildReverse()
    {
        var map = new Dictionary<DownloadCategory, string[]>();

        foreach (var group in ByExtension.GroupBy(pair => pair.Value))
            map[group.Key] = group.Select(pair => pair.Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        return map;
    }
}
