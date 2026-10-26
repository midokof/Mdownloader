using NovaDownload.BrowserIntegration.Protocol;

namespace NovaDownload.BrowserIntegration.Models;

/// <summary>Response returned to the extension, over the pipe and then over native messaging.</summary>
public sealed class BrowserResponse
{
    public bool Ok { get; set; }

    public string Code { get; set; } = ResponseCodes.Ok;

    public string? Action { get; set; }

    public string? Message { get; set; }

    /// <summary>Engine id of the accepted transfer — the same id the desktop list uses.</summary>
    public Guid? DownloadId { get; set; }

    /// <summary>File name the engine actually resolved (server name or sanitized hint).</summary>
    public string? FileName { get; set; }

    public string? DestinationPath { get; set; }

    public bool Duplicate { get; set; }

    public int ProtocolVersion { get; set; } = BrowserActions.ProtocolVersion;

    public string? AppVersion { get; set; }

    public string? DefaultDirectory { get; set; }

    public bool IntegrationEnabled { get; set; } = true;

    public int ActiveDownloads { get; set; }

    public int FinishedDownloads { get; set; }

    /// <summary>Desktop application that produced the response: "novadownload-app".</summary>
    public string? Server { get; set; }

    public static BrowserResponse Fail(string code, string message, string? action = null) =>
        new() { Ok = false, Code = code, Message = message, Action = action };
}
