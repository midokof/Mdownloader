using NovaDownload.BrowserIntegration.Models;

namespace NovaDownload.BrowserIntegration.Validation;

/// <summary>Normalized, trusted shape of an inbound request.</summary>
public sealed record ValidatedRequest(
    string Action,
    string? Url,
    string? FileName,
    string? Directory,
    string? PageUrl,
    string? MediaType,
    string? Browser,
    string? RequestId);

public sealed record ValidationResult(bool IsValid, string Code, string Message, ValidatedRequest? Request)
{
    public static ValidationResult Ok(ValidatedRequest request) => new(true, Protocol.ResponseCodes.Ok, string.Empty, request);

    public static ValidationResult Fail(string code, string message) => new(false, code, message, null);
}
