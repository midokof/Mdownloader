namespace NovaDownload.BrowserIntegration.Protocol;

/// <summary>Stable machine-readable outcomes. The extension maps these to user-facing messages.</summary>
public static class ResponseCodes
{
    public const string Ok = "OK";
    public const string Duplicate = "DUPLICATE";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnknownAction = "UNKNOWN_ACTION";
    public const string UnsupportedScheme = "UNSUPPORTED_SCHEME";
    public const string UnsafeFileName = "UNSAFE_FILENAME";
    public const string InvalidDirectory = "INVALID_DIRECTORY";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string MalformedMessage = "MALFORMED_MESSAGE";
    public const string AppUnavailable = "APP_UNAVAILABLE";
    public const string Disabled = "DISABLED";
    public const string InternalError = "INTERNAL_ERROR";
}
