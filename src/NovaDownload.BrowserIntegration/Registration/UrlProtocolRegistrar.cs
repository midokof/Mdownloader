using Microsoft.Win32;

namespace NovaDownload.BrowserIntegration.Registration;

/// <summary>
/// URL scheme registration for this user, under <c>HKCU\Software\Classes</c>, so no administrator
/// is required. Two schemes:
/// <list type="bullet">
///   <item><c>novadownload://</c> - the documented fallback for a browser that cannot use native
///     messaging (for example a link clicked outside the extension).</item>
///   <item><c>magnet:</c> - BitTorrent links clicked anywhere in Windows, including a browser
///     that has no extension installed.</item>
/// </list>
/// The installer registers both for the machine; this class keeps them true for the user who
/// turns the setting on or off at runtime.
/// </summary>
public static class UrlProtocolRegistrar
{
    private static string ProtocolKey => $@"Software\Classes\{BrowserIntegrationInfo.UrlProtocol}";

    private static string MagnetKey => @"Software\Classes\magnet";

    /// <summary>Builds the protocol URL an extension or a page can hand to Windows.</summary>
    public static string BuildProtocolUrl(string url, string? fileName = null, string? directory = null)
    {
        var query = $"url={Uri.EscapeDataString(url)}";
        if (!string.IsNullOrWhiteSpace(fileName)) query += $"&filename={Uri.EscapeDataString(fileName)}";
        if (!string.IsNullOrWhiteSpace(directory)) query += $"&directory={Uri.EscapeDataString(directory)}";
        return $"{BrowserIntegrationInfo.UrlProtocol}://download?{query}";
    }

    public static bool Register(string appExecutablePath) =>
        RegisterScheme(ProtocolKey, "URL:NovaDownload Protocol", appExecutablePath);

    public static void Unregister() => DeleteScheme(ProtocolKey);

    public static string? RegisteredCommand() => ReadCommand(ProtocolKey);

    public static bool IsRegistered() => !string.IsNullOrWhiteSpace(RegisteredCommand());

    // ---- magnet: ------------------------------------------------------------------

    /// <summary>
    /// Makes NovaDownload the handler for <c>magnet:</c> links. Windows keeps a single handler per
    /// scheme, so this takes magnet links away from any other BitTorrent client the user has.
    /// </summary>
    public static bool RegisterMagnet(string appExecutablePath) =>
        RegisterScheme(MagnetKey, "URL:NovaDownload magnet handler", appExecutablePath);

    public static void UnregisterMagnet() => DeleteScheme(MagnetKey);

    public static string? RegisteredMagnetCommand() => ReadCommand(MagnetKey);

    /// <summary>True when this application owns the <c>magnet:</c> scheme right now.</summary>
    public static bool IsMagnetRegistered()
    {
        var command = RegisteredMagnetCommand();
        if (string.IsNullOrWhiteSpace(command)) return false;

        // Another client may hold the scheme; only an entry pointing at this executable counts.
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return true;

        return command.Contains(
            Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase);
    }

    // ---- .torrent files ------------------------------------------------------------

    private const string TorrentProgId = "NovaDownload.Torrent";

    private static string TorrentExtensionKey => @"Software\Classes\.torrent";

    private static string TorrentProgIdKey => $@"Software\Classes\{TorrentProgId}";

    /// <summary>
    /// Points <c>.torrent</c> at this application for the current user. Windows keeps a
    /// per-user choice once the user has picked a handler, so this is an offer, not a takeover:
    /// it never touches <c>UserChoice</c>.
    /// </summary>
    public static bool RegisterTorrentFiles(string appExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(appExecutablePath) || !File.Exists(appExecutablePath))
            throw new FileNotFoundException("Application executable not found.", appExecutablePath);

        using (var progId = Registry.CurrentUser.CreateSubKey(TorrentProgIdKey, writable: true)
                            ?? throw new InvalidOperationException("Cannot create the .torrent ProgID key."))
        {
            progId.SetValue(null, "NovaDownload torrent", RegistryValueKind.String);
            using (var icon = progId.CreateSubKey("DefaultIcon", writable: true))
                icon?.SetValue(null, $"\"{appExecutablePath}\",0", RegistryValueKind.String);

            using var command = progId.CreateSubKey(@"shell\open\command", writable: true)
                                ?? throw new InvalidOperationException("Cannot create the .torrent command key.");
            command.SetValue(null, $"\"{appExecutablePath}\" \"%1\"", RegistryValueKind.String);
        }

        using var extension = Registry.CurrentUser.CreateSubKey(TorrentExtensionKey, writable: true)
                              ?? throw new InvalidOperationException("Cannot create the .torrent extension key.");
        extension.SetValue(null, TorrentProgId, RegistryValueKind.String);
        extension.SetValue("Content Type", "application/x-bittorrent", RegistryValueKind.String);

        return true;
    }

    public static void UnregisterTorrentFiles()
    {
        DeleteScheme(TorrentExtensionKey);
        DeleteScheme(TorrentProgIdKey);
    }

    public static string? RegisteredTorrentCommand() => ReadCommand(TorrentProgIdKey);

    /// <summary>True when <c>.torrent</c> resolves to this executable on this user.</summary>
    public static bool IsTorrentFileRegistered()
    {
        var command = RegisteredTorrentCommand();
        if (string.IsNullOrWhiteSpace(command)) return false;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return true;

        return command.Contains(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase);
    }

    // ---- shared -------------------------------------------------------------------

    private static bool RegisterScheme(string keyPath, string label, string appExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(appExecutablePath) || !File.Exists(appExecutablePath))
            throw new FileNotFoundException("Application executable not found.", appExecutablePath);

        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot create the URL protocol registry key.");
        key.SetValue(null, label, RegistryValueKind.String);
        key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

        using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
            icon?.SetValue(null, $"\"{appExecutablePath}\",0", RegistryValueKind.String);

        using var command = key.CreateSubKey(@"shell\open\command", writable: true)
            ?? throw new InvalidOperationException("Cannot create the URL protocol command key.");
        command.SetValue(null, $"\"{appExecutablePath}\" \"%1\"", RegistryValueKind.String);
        return true;
    }

    private static void DeleteScheme(string keyPath)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
    }

    private static string? ReadCommand(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath + @"\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
