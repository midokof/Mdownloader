using NovaDownload.Core.Enums;
using NovaDownload.Core.Models;

namespace NovaDownload.Core.Common;

/// <summary>
/// The list predicate, in one place: the category pane, the search box and the F3 walk all call
/// this, so what the list shows and what "next match" finds can never drift apart.
/// </summary>
public static class DownloadFilter
{
    /// <summary>
    /// True when <paramref name="item"/> belongs to the selected scope. A scope with no category
    /// (anything but <see cref="DownloadScope.Category"/>) ignores <paramref name="category"/>.
    /// </summary>
    public static bool MatchesScope(DownloadItem item, DownloadScope scope, DownloadCategory category,
        string? customCategory = null) => scope switch
    {
        DownloadScope.All => true,
        DownloadScope.Unfinished => item.IsOngoing,
        DownloadScope.Finished => item.IsFinished,
        DownloadScope.Queue => item.Status == DownloadStatus.Queued,
        DownloadScope.Category => item.CustomCategory.Length == 0 && item.Category == category,
        DownloadScope.Custom => item.CustomCategory.Length > 0 &&
                                string.Equals(item.CustomCategory, customCategory, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    /// <summary>Case-insensitive match on name, URL, folder and description.</summary>
    public static bool MatchesSearch(DownloadItem item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var needle = search.Trim();
        return Contains(item.FileName, needle)
               || Contains(item.Url, needle)
               || Contains(item.Folder, needle)
               || Contains(item.Description, needle);
    }

    public static bool Matches(DownloadItem item, DownloadScope scope, DownloadCategory category, string? search,
        string? customCategory = null) =>
        MatchesScope(item, scope, category, customCategory) && MatchesSearch(item, search);

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
