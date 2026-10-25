namespace NovaDownload.Core.Common;

/// <summary>
/// The one definition of "this string is a BitTorrent source". The browser bridge gate, the
/// command-line parser and the download router all answer with this code, so a magnet link can
/// never be accepted on one path and refused on another.
/// </summary>
public static class TorrentSourceRules
{
    public const string MagnetScheme = "magnet";

    /// <summary>Magnet links carrying a long tracker list routinely pass 2048 characters.</summary>
    public const int MaxSourceLength = 8192;

    private const string InfoHashPrefix = "urn:btih:";

    /// <summary>True for a magnet link, a local .torrent path or an http(s) URL ending in .torrent.</summary>
    public static bool IsTorrent(string? source) => TryNormalize(source, out _, out _);

    /// <summary>True when the source is a magnet URI (metadata has to be fetched from the swarm).</summary>
    public static bool IsMagnet(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        source.Trim().Trim('"').StartsWith($"{MagnetScheme}:", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the source is a remote .torrent that has to be fetched over HTTP first.</summary>
    public static bool IsRemoteTorrentFile(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var value = source.Trim().Trim('"');
        if (!IsHttp(value)) return false;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a source and returns the form to hand to the engine: a magnet URI unchanged,
    /// a .torrent URL rebuilt without its fragment, a local path made absolute.
    /// </summary>
    public static bool TryNormalize(string? source, out string normalized, out string reason)
    {
        normalized = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            reason = "Missing source.";
            return false;
        }

        var value = source.Trim().Trim('"');

        if (value.Length > MaxSourceLength)
        {
            reason = $"Source longer than {MaxSourceLength} characters.";
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                reason = "Source contains control characters.";
                return false;
            }
        }

        if (value.StartsWith($"{MagnetScheme}:", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasInfoHash(value))
            {
                reason = "Magnet link carries no BitTorrent info hash (xt=urn:btih:...).";
                return false;
            }

            normalized = value;
            return true;
        }

        if (IsHttp(value))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Only an HTTP(S) link whose path ends in .torrent can be fetched.";
                return false;
            }

            normalized = new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
            return true;
        }

        if (!value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Not a magnet link, a .torrent file or a .torrent URL.";
            return false;
        }

        if (!File.Exists(value))
        {
            reason = "The .torrent file does not exist.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value);
        }
        catch (Exception)
        {
            reason = "The .torrent path is not usable.";
            return false;
        }

        return true;
    }

    /// <summary>Reads one parameter out of a magnet query string, percent-decoded.</summary>
    public static string? ReadParameter(string magnet, string key)
    {
        var query = magnet.Split('?', 2).ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(query)) return null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            if (!part.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
        }

        return null;
    }

    private static bool IsHttp(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A magnet without an info hash cannot start a transfer; refuses it early.</summary>
    private static bool HasInfoHash(string magnet)
    {
        var query = magnet.Split('?', 2).ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(query)) return false;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            if (!part.AsSpan(0, separator).Equals("xt", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (!value.StartsWith(InfoHashPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var hash = value[InfoHashPrefix.Length..].Trim();
            // 40 hex characters (v1) or 32 base32 characters (BEP 9 base32 form).
            if (hash.Length == 40 && hash.All(Uri.IsHexDigit)) return true;
            if (hash.Length == 32 && hash.All(c => char.IsAsciiLetterOrDigit(c))) return true;
        }

        return false;
    }
}
