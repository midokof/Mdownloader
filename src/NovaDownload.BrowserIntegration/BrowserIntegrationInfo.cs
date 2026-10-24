namespace NovaDownload.BrowserIntegration;

/// <summary>Values shared by the desktop application, the native messaging host and the installer.</summary>
public static class BrowserIntegrationInfo
{
    /// <summary>Native messaging host name. Must match the manifests and the extension's <c>connectNative</c> call.</summary>
    public const string HostName = "com.novadownload.host";

    /// <summary>Firefox extension identifier (native messaging uses ids, not Chromium's key-derived ids).</summary>
    public const string FirefoxExtensionId = "novadownload@vanta.local";

    /// <summary>
    /// Chromium extension id derived from <see cref="ChromiumExtensionKey"/> with
    /// <see cref="Registration.ChromiumExtensionId.FromPublicKey"/>.
    /// </summary>
    public const string ChromiumExtensionId = "pnemgdlefleemkhignbgaclaelffmbah";

    /// <summary>
    /// SubjectPublicKeyInfo (DER, base64) embedded as <c>"key"</c> in the Chromium manifests so the
    /// unpacked extension gets a stable id. Public information only — no private key is shipped.
    /// </summary>
    public const string ChromiumExtensionKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAny8RMtNRm1ycKc5GPCG8" +
        "EU6F2K5Jph+0LGW3/UoNrN7HWxH5nwm1hM07vDD2QK1rpWFQ7lO3bnP4DKfGyGW3" +
        "VjlqfJgrIJ48xU5wxEUEYsx94OtFcyOc16q7bTidBzDVdjlHde+p/HFdfXOq6O6k" +
        "6J+toActxBtHARdCxZ4LCMaL3Qah1WYJTQyBb8x51Zymc7cwyyeOtALY1icX3CBv" +
        "4UGmHe8gfMPUZvDbXWaGVfwNNcXJSWdxzKL2SRLbphr6bpWakDgQnXOv0HDJyT1l" +
        "pdYJsI37N/4c7m7h89EWNKJBkVrGHyP4n/tQFXNws+zJI4JZRg+HCwWRGTpu45rP" +
        "2QIDAQAB";

    /// <summary>Registry key under HKCU where each Chromium browser looks for native messaging hosts.</summary>
    public static string ChromiumRegistryKey(string vendorFolder) =>
        $@"Software\{vendorFolder}\NativeMessagingHosts\{HostName}";

    public const string ChromeVendorFolder = "Google\\Chrome";
    public const string EdgeVendorFolder = "Microsoft\\Edge";
    public const string FirefoxRegistryKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;

    /// <summary>Last-resort URL protocol handled directly by the desktop application.</summary>
    public const string UrlProtocol = "novadownload";

    /// <summary>
    /// Installer <c>AppId</c> (Inno Setup). Must match <c>installer/NovaDownload.iss</c>; the unit
    /// tests read that file and fail if the two ever drift apart.
    /// </summary>
    public const string InstallerAppId = "{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}";

    /// <summary>
    /// Uninstall registry key Inno Setup creates for <see cref="InstallerAppId"/> under
    /// <c>Software\Microsoft\Windows\CurrentVersion\Uninstall</c>.
    /// </summary>
    public const string InstallerUninstallKey = InstallerAppId + "_is1";

    /// <summary>Folder the installer uses for the application (per-user mode).</summary>
    public const string PerUserInstallFolder = @"%LOCALAPPDATA%\Programs\NovaDownload";

    /// <summary>Folder the installer uses for the browser extension packages (never uninstalled).</summary>
    public const string ExtensionInstallFolder = @"%LOCALAPPDATA%\NovaDownload\extensions";

    /// <summary>Folder holding the user's settings and download history (never uninstalled).</summary>
    public const string UserDataFolder = @"%APPDATA%\NovaDownload";
}
