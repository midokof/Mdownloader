using Microsoft.Win32;

namespace NovaDownload.BrowserIntegration.Registration;

/// <summary>
/// <c>novadownload://</c> URL scheme registration. This is the documented fallback for contexts
/// where the browser cannot use native messaging (for example a link the user clicks outside the
/// extension). Writes to <c>HKCU\Software\Classes</c> so no administrator is required; the
/// installer additionally registers it for all users.
/// </summary>
public static class UrlProtocolRegistrar
{
    private static string ProtocolKey => $@"Software\Classes\{BrowserIntegrationInfo.UrlProtocol}";

    /// <summary>Builds the protocol URL an extension or a page can hand to Windows.</summary>
    public static string BuildProtocolUrl(string url, string? fileName = null, string? directory = null)
    {
        var query = $"url={Uri.EscapeDataString(url)}";
        if (!string.IsNullOrWhiteSpace(fileName)) query += $"&filename={Uri.EscapeDataString(fileName)}";
        if (!string.IsNullOrWhiteSpace(directory)) query += $"&directory={Uri.EscapeDataString(directory)}";
        return $"{BrowserIntegrationInfo.UrlProtocol}://download?{query}";
    }

    public static bool Register(string appExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(appExecutablePath) || !File.Exists(appExecutablePath))
            throw new FileNotFoundException("Application executable not found.", appExecutablePath);

        using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey, writable: true)
            ?? throw new InvalidOperationException("Cannot create the URL protocol registry key.");
        key.SetValue(null, "URL:NovaDownload Protocol", RegistryValueKind.String);
        key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

        using var command = key.CreateSubKey(@"shell\open\command", writable: true)
            ?? throw new InvalidOperationException("Cannot create the URL protocol command key.");
        command.SetValue(null, $"\"{appExecutablePath}\" \"%1\"", RegistryValueKind.String);
        return true;
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false); } catch { }
    }

    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRegistered() => !string.IsNullOrWhiteSpace(RegisteredCommand());
}
