using System.Text.RegularExpressions;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.Core.Common;

namespace NovaDownload.BrowserIntegration.Validation;

/// <summary>
/// The single gate between a browser extension and the download engine. Nothing from the
/// extension may reach <c>IDownloadService</c> or the file system without passing through here.
/// </summary>
public static partial class DownloadRequestValidator
{
    public const int MaxUrlLength = 2048;
    public const int MaxPageUrlLength = 2048;
    public const int MaxDirectoryLength = 260;
    public const int MaxRequestIdLength = 64;
    public const int MaxBrowserLength = 32;
    public const int MaxMediaTypeLength = 100;

    /// <summary>Schemes the engine can actually fetch. Everything else is refused with a reason.</summary>
    private static readonly string[] AllowedSchemes = ["http", "https"];

    private static readonly string[] KnownUnsupportedSchemes =
        ["blob", "data", "javascript", "file", "filesystem", "chrome", "edge", "about", "view-source", "ms-appx", "ms-appdata"];

    [GeneratedRegex(@"^[\w.+-]{1,64}/[\w.+-]{1,64}$")]
    private static partial Regex MediaTypePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,32}$")]
    private static partial Regex BrowserPattern();

    public static ValidationResult Validate(BrowserRequest? request)
    {
        if (request is null) return ValidationResult.Fail(ResponseCodes.InvalidRequest, "Empty request.");

        var action = request.Action?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(action))
            return ValidationResult.Fail(ResponseCodes.InvalidRequest, "Missing action.");
        if (!BrowserActions.IsKnown(action))
            return ValidationResult.Fail(ResponseCodes.UnknownAction, $"Unsupported action '{Trim(request.Action, 32)}'.");

        string? requestId = null;
        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            var candidate = request.RequestId.Trim();
            if (candidate.Length > MaxRequestIdLength || !RequestIdPattern().IsMatch(candidate))
                return ValidationResult.Fail(ResponseCodes.InvalidRequest, "Invalid request id.");
            requestId = candidate;
        }

        string? browser = null;
        if (!string.IsNullOrWhiteSpace(request.Browser))
        {
            var candidate = request.Browser.Trim();
            if (candidate.Length <= MaxBrowserLength && BrowserPattern().IsMatch(candidate))
                browser = candidate;
        }

        // A torrent source is gated here too, but with its own shape rules: the media URL
        // validator refuses anything that is not http(s), and a magnet link is something the
        // engine can fetch perfectly well.
        if (action == BrowserActions.Torrent)
        {
            var source = ValidateTorrentSource(request.Url, out var sourceError);
            if (source is null) return ValidationResult.Fail(sourceError.Code, sourceError.Message);

            var torrentDirectory = ValidateDirectory(request.Directory, out var torrentDirectoryError);
            if (!string.IsNullOrEmpty(torrentDirectoryError.Code))
                return ValidationResult.Fail(torrentDirectoryError.Code, torrentDirectoryError.Message);

            return ValidationResult.Ok(new ValidatedRequest(
                action, source, null, torrentDirectory, null, null, browser, requestId));
        }

        // Non-download actions never carry a payload that touches the engine.
        if (action != BrowserActions.Download)
            return ValidationResult.Ok(new ValidatedRequest(action, null, null, null, null, null, browser, requestId));

        var url = ValidateUrl(request.Url, out var urlError);
        if (url is null) return ValidationResult.Fail(urlError.Code, urlError.Message);

        var fileName = ValidateFileName(request.FileName, out var nameError);
        if (!string.IsNullOrEmpty(nameError.Code)) return ValidationResult.Fail(nameError.Code, nameError.Message);

        var directory = ValidateDirectory(request.Directory, out var directoryError);
        if (!string.IsNullOrEmpty(directoryError.Code)) return ValidationResult.Fail(directoryError.Code, directoryError.Message);

        string? pageUrl = null;
        if (!string.IsNullOrWhiteSpace(request.PageUrl))
        {
            pageUrl = ValidateUrl(request.PageUrl, out _);
            if (pageUrl is { Length: > MaxPageUrlLength }) pageUrl = null;
        }

        var mediaType = request.MediaType is { Length: > 0 } hint && hint.Length <= MaxMediaTypeLength &&
                        MediaTypePattern().IsMatch(hint.Trim())
            ? hint.Trim().ToLowerInvariant()
            : null;

        return ValidationResult.Ok(new ValidatedRequest(action, url, fileName, directory, pageUrl, mediaType, browser, requestId));
    }

    /// <summary>
    /// Validates a BitTorrent source: a magnet URI carrying an info hash, an existing local
    /// <c>.torrent</c> file, or an http(s) URL whose path ends in <c>.torrent</c>.
    /// </summary>
    public static string? ValidateTorrentSource(string? candidate, out (string Code, string Message) error)
    {
        error = default;

        if (!TorrentSourceRules.TryNormalize(candidate, out var normalized, out var reason))
        {
            error = (ResponseCodes.InvalidRequest, reason);
            return null;
        }

        return normalized;
    }

    /// <summary>Validates and normalizes an absolute http/https URL (fragment removed).</summary>
    public static string? ValidateUrl(string? candidate, out (string Code, string Message) error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = (ResponseCodes.InvalidRequest, "Missing URL.");
            return null;
        }

        var value = candidate.Trim();
        if (value.Length > MaxUrlLength)
        {
            error = (ResponseCodes.InvalidRequest, $"URL longer than {MaxUrlLength} characters.");
            return null;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                error = (ResponseCodes.InvalidRequest, "URL contains control characters.");
                return null;
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = (ResponseCodes.InvalidRequest, "URL is not absolute.");
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (!AllowedSchemes.Contains(scheme))
        {
            var known = KnownUnsupportedSchemes.Contains(scheme);
            error = (ResponseCodes.UnsupportedScheme, known
                ? $"'{scheme}:' URLs cannot be fetched by the desktop application. Only direct http/https resources are supported."
                : $"Unsupported URL scheme '{Trim(scheme, 16)}'. Only http and https are accepted.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = (ResponseCodes.InvalidRequest, "URL has no host.");
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = (ResponseCodes.InvalidRequest, "URLs carrying credentials are not accepted.");
            return null;
        }

        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    /// <summary>Returns the sanitized name, or fails when the hint contains path separators.</summary>
    public static string? ValidateFileName(string? candidate, out (string Code, string Message) error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        if (FileNameSanitizer.ContainsPathTraversal(candidate))
        {
            error = (ResponseCodes.UnsafeFileName, "File name may not contain path separators or parent references.");
            return null;
        }

        if (FileNameSanitizer.IsReserved(candidate))
        {
            error = (ResponseCodes.UnsafeFileName, "File name is reserved by Windows.");
            return null;
        }

        var sanitized = FileNameSanitizer.Sanitize(candidate);
        if (sanitized.Length == 0)
        {
            error = (ResponseCodes.UnsafeFileName, "File name is not usable.");
            return null;
        }

        return sanitized;
    }

    /// <summary>Local, fully qualified destination folder. UNC and device paths are refused.</summary>
    public static string? ValidateDirectory(string? candidate, out (string Code, string Message) error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var value = candidate.Trim().Trim('"');
        if (value.Length > MaxDirectoryLength)
        {
            error = (ResponseCodes.InvalidDirectory, $"Destination path longer than {MaxDirectoryLength} characters.");
            return null;
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            error = (ResponseCodes.InvalidDirectory, "Network and device paths are not accepted as a destination.");
            return null;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            error = (ResponseCodes.InvalidDirectory, "Destination must be an absolute local path.");
            return null;
        }

        foreach (var segment in value.Split('\\', '/'))
        {
            if (segment == "..")
            {
                error = (ResponseCodes.InvalidDirectory, "Destination may not contain '..' segments.");
                return null;
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception)
        {
            error = (ResponseCodes.InvalidDirectory, "Destination path is not valid.");
            return null;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            error = (ResponseCodes.InvalidDirectory, "Destination drive is not available.");
            return null;
        }

        return full.TrimEnd('\\');
    }

    private static string Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
