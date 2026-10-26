using NovaDownload.Core.Common;

namespace NovaDownload.DownloadEngine.Torrenting;

/// <summary>
/// Decides which engine owns a source string. Kept separate from both services so the router,
/// the UI validation and the tests all answer the same question the same way. The rules
/// themselves live in <see cref="TorrentSourceRules"/> because the browser bridge gate needs the
/// same verdict without taking a dependency on the engine.
/// </summary>
public static class TorrentSource
{
    public const string MagnetScheme = TorrentSourceRules.MagnetScheme;

    /// <summary>True for a magnet link, a local .torrent path or an http(s) URL ending in .torrent.</summary>
    public static bool IsTorrent(string? source) => TorrentSourceRules.IsTorrent(source);

    /// <summary>True when the source is a magnet URI (metadata has to be fetched from the swarm).</summary>
    public static bool IsMagnet(string? source) => TorrentSourceRules.IsMagnet(source);

    /// <summary>True when the source is a remote .torrent that has to be fetched over HTTP first.</summary>
    public static bool IsRemoteTorrentFile(string? source) => TorrentSourceRules.IsRemoteTorrentFile(source);

    /// <summary>The source in the form the engine expects, or null when it is not a usable torrent source.</summary>
    public static string? Normalize(string? source) =>
        TorrentSourceRules.TryNormalize(source, out var normalized, out _) ? normalized : null;

    /// <summary>Display name for a source that has not produced metadata yet.</summary>
    public static string DisplayName(string source)
    {
        var value = source.Trim();

        if (IsMagnet(value))
        {
            var name = TorrentSourceRules.ReadParameter(value, "dn");
            if (!string.IsNullOrWhiteSpace(name)) return Sanitize(name!);
            var hash = TorrentSourceRules.ReadParameter(value, "xt");
            return string.IsNullOrWhiteSpace(hash)
                ? "torrent"
                : Sanitize(hash!.Replace("urn:btih:", "", StringComparison.OrdinalIgnoreCase));
        }

        if (IsRemoteTorrentFile(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return Sanitize(Path.GetFileNameWithoutExtension(uri.AbsolutePath));

        var local = Path.GetFileNameWithoutExtension(value);
        return string.IsNullOrWhiteSpace(local) ? "torrent" : Sanitize(local);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        return name.Length == 0 ? "torrent" : name;
    }
}
