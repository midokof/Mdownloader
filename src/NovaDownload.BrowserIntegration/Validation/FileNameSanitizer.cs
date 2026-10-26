namespace NovaDownload.BrowserIntegration.Validation;

/// <summary>
/// Windows-safe file name normalization. Deterministic and side-effect free so the same rules
/// run in the host, in the desktop application and in tests.
/// </summary>
public static class FileNameSanitizer
{
    public const int MaxFileNameLength = 180;

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>True when the raw hint tries to escape its directory (separators or parent refs).</summary>
    public static bool ContainsPathTraversal(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains(':')) return true;
        foreach (var c in candidate)
            if (char.IsControl(c)) return true;
        var dots = candidate.Replace(" ", string.Empty);
        return dots is "." or "..";
    }

    public static bool IsReserved(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return ReservedNames.Contains(stem) || ReservedNames.Contains(name);
    }

    /// <summary>Returns a usable file name, or an empty string when nothing safe is left.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var value = name.Trim().Trim('"').Trim();
        value = value.Replace('/', '_').Replace('\\', '_');

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            builder.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }

        var cleaned = builder.ToString();
        // Windows silently drops trailing dots and spaces; do it explicitly and predictably.
        cleaned = cleaned.TrimEnd('.', ' ');
        while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
        cleaned = cleaned.Trim();

        if (cleaned.Length == 0 || cleaned is "." or "..") return string.Empty;

        if (IsReserved(cleaned)) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 16) extension = string.Empty;
            var stemLength = Math.Max(1, MaxFileNameLength - extension.Length);
            cleaned = string.Concat(cleaned.AsSpan(0, Math.Min(stemLength, cleaned.Length)), extension);
            cleaned = cleaned.TrimEnd('.', ' ');
        }

        return cleaned;
    }
}
