using Microsoft.Win32;

namespace NovaDownload.BrowserIntegration.Registration;

public enum BrowserKind
{
    Chrome,
    Edge,
    Firefox
}

public sealed record BrowserTarget(
    BrowserKind Kind,
    string DisplayName,
    string RegistryKey,
    string[] ExecutableCandidates);

/// <summary>Known browsers, their native messaging registry key and where their binary usually lives.</summary>
public static class BrowserCatalog
{
    public static IReadOnlyList<BrowserTarget> Targets { get; } =
    [
        new BrowserTarget(BrowserKind.Chrome, "Google Chrome",
            BrowserIntegrationInfo.ChromiumRegistryKey(BrowserIntegrationInfo.ChromeVendorFolder),
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            ]),
        new BrowserTarget(BrowserKind.Edge, "Microsoft Edge",
            BrowserIntegrationInfo.ChromiumRegistryKey(BrowserIntegrationInfo.EdgeVendorFolder),
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
            ]),
        new BrowserTarget(BrowserKind.Firefox, "Mozilla Firefox",
            BrowserIntegrationInfo.FirefoxRegistryKey,
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Mozilla Firefox\firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Mozilla Firefox\firefox.exe")
            ])
    ];

    /// <summary>Executable of an installed browser, or null when it is not present on this machine.</summary>
    public static string? FindExecutable(BrowserTarget target) =>
        target.ExecutableCandidates.FirstOrDefault(File.Exists);

    public static bool IsInstalled(BrowserTarget target) => FindExecutable(target) != null;

    /// <summary>Product version reported by the browser binary, when it can be read.</summary>
    public static string? TryGetVersion(string? executable)
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return null;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Registry path currently pointing at a native messaging manifest, if any.</summary>
    public static string? ReadRegisteredManifest(string registryKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryKey);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
