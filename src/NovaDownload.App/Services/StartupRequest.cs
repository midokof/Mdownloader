using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Validation;
using NovaDownload.Core.Common;

namespace NovaDownload.App.Services;

/// <summary>
/// A download handed to the application from outside: the <c>novadownload://</c> protocol, the
/// <c>magnet:</c> protocol, a <c>.torrent</c> path or an explicit <c>--url</c> / <c>--torrent</c>
/// switch. Torrent sources are routed to the BitTorrent engine, everything else to the HTTP one.
/// </summary>
public sealed record StartupRequest(string Url, string? FileName, string? Directory, string Source)
{
    /// <summary>True when the source belongs to the swarm engine rather than the HTTP engine.</summary>
    public bool IsTorrent => TorrentSourceRules.IsTorrent(Url);

    /// <summary>Parses the command line. Returns null when there is nothing to download.</summary>
    public static StartupRequest? Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--url" or "-u" && i + 1 < args.Length)
                return FromPlainUrl(args[i + 1], "command line --url");

            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                return FromPlainUrl(arg[6..], "command line --url");

            if (arg is "--torrent" or "-t" && i + 1 < args.Length)
                return FromTorrentSource(args[i + 1], "command line --torrent");

            if (arg.StartsWith("--torrent=", StringComparison.OrdinalIgnoreCase))
                return FromTorrentSource(arg[10..], "command line --torrent");

            if (arg.StartsWith($"{BrowserIntegrationInfo.UrlProtocol}://", StringComparison.OrdinalIgnoreCase))
                return FromProtocolUrl(arg);

            // A magnet link reaches the process bare when Windows hands over a magnet: click.
            if (TorrentSourceRules.IsMagnet(arg))
                return FromTorrentSource(arg, "magnet: protocol");

            if (arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                return FromTorrentSource(arg, "command line .torrent");
        }

        return null;
    }

    private static StartupRequest? FromPlainUrl(string url, string source) =>
        string.IsNullOrWhiteSpace(url) ? null : new StartupRequest(url.Trim(), null, null, source);

    private static StartupRequest? FromTorrentSource(string source, string origin)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        // Rejects a magnet without an info hash and a .torrent path that does not exist before the
        // window is even created.
        if (!TorrentSourceRules.TryNormalize(source, out var normalized, out _)) return null;

        return new StartupRequest(normalized, null, null, origin);
    }

    /// <summary>
    /// <c>novadownload://download?url=...&amp;filename=...&amp;directory=...</c> or the path form
    /// <c>novadownload://download/&lt;encoded url&gt;</c>.
    /// </summary>
    public static StartupRequest? FromProtocolUrl(string protocolUrl)
    {
        if (!Uri.TryCreate(protocolUrl, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, BrowserIntegrationInfo.UrlProtocol, StringComparison.OrdinalIgnoreCase)) return null;

        var action = uri.Host.ToLowerInvariant();
        if (action is not ("download" or "open")) return null;

        string? url = null;
        string? fileName = null;
        string? directory = null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            var name = pair[..index].ToLowerInvariant();
            var value = Uri.UnescapeDataString(pair[(index + 1)..]);
            switch (name)
            {
                case "url": url = value; break;
                case "filename": fileName = value; break;
                case "directory": directory = value; break;
            }
        }

        if (url is null && action == "download" && uri.AbsolutePath.Length > 1)
            url = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        if (string.IsNullOrWhiteSpace(url)) return null;
        return new StartupRequest(url.Trim(), fileName, directory, "novadownload:// protocol");
    }

    public BrowserRequest ToBrowserRequest()
    {
        var torrent = IsTorrent;
        return new BrowserRequest
        {
            Action = torrent ? BrowserActions.Torrent : BrowserActions.Download,
            Url = Url,
            // A torrent names its own files once the metadata arrives; a hint would fight the swarm.
            FileName = torrent ? null : FileName,
            Directory = Directory,
            Browser = torrent ? "magnet-protocol" : "url-protocol",
            ExtensionVersion = "1.0.0",
            RequestId = $"protocol:{Guid.NewGuid():n}"
        };
    }

    /// <summary>
    /// Hands the request to an already running instance. Returns true when that instance answered,
    /// in which case this process must exit without opening a second window.
    /// </summary>
    public async Task<(bool Forwarded, BrowserResponse? Response)> TryForwardAsync(CancellationToken cancellationToken = default)
    {
        if (!await BrowserBridgeProtocol.IsServerAvailableAsync(timeout: TimeSpan.FromMilliseconds(600), cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            return (false, null);

        var response = await BrowserBridgeProtocol
            .SendRequestAsync(ToBrowserRequest(), connectTimeout: TimeSpan.FromSeconds(2), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (true, response);
    }

    /// <summary>Human-readable validation of the URL, used before queueing.</summary>
    public bool IsValid => DownloadRequestValidator.Validate(ToBrowserRequest()).IsValid;
}
