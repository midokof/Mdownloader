namespace NovaDownload.BrowserIntegration.Models;

/// <summary>
/// Request sent by a browser extension. Every field is untrusted input: nothing reaches the
/// download engine before <see cref="Validation.DownloadRequestValidator"/> has seen it.
/// </summary>
public sealed class BrowserRequest
{
    public string? Action { get; set; }

    /// <summary>Absolute http/https URL of the resource to fetch.</summary>
    public string? Url { get; set; }

    /// <summary>Optional file name hint. Sanitized; path separators are rejected.</summary>
    public string? FileName { get; set; }

    /// <summary>Optional absolute local destination folder. Never a UNC path.</summary>
    public string? Directory { get; set; }

    /// <summary>Page the resource was found on. Validated, used for diagnostics only.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Advisory MIME type reported by the page or the network layer.</summary>
    public string? MediaType { get; set; }

    /// <summary>"chrome" | "edge" | "firefox" — diagnostics only.</summary>
    public string? Browser { get; set; }

    public string? ExtensionVersion { get; set; }

    /// <summary>Idempotency key: a repeated id inside the dedupe window is treated as the same request.</summary>
    public string? RequestId { get; set; }

    public int ProtocolVersion { get; set; } = Protocol.BrowserActions.ProtocolVersion;
}
