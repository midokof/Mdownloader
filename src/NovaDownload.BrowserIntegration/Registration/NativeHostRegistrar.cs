using System.Text.Json;
using Microsoft.Win32;

namespace NovaDownload.BrowserIntegration.Registration;

public sealed record BrowserRegistration(
    BrowserKind Kind,
    string DisplayName,
    string BrowserPath,
    string? BrowserVersion,
    string ManifestPath,
    string RegistryKey,
    bool Registered,
    string Detail);

public sealed record RegistrationReport(IReadOnlyList<BrowserRegistration> Entries, string ManifestDirectory, string HostExecutable)
{
    public bool AnyRegistered => Entries.Any(entry => entry.Registered);

    public IEnumerable<BrowserRegistration> Registered => Entries.Where(entry => entry.Registered);
}

/// <summary>
/// Writes the native messaging host manifests and the per-user registry keys that make Chrome,
/// Edge and Firefox able to launch the NovaDownload host. Per-user <c>HKCU</c> only: no admin
/// rights, nothing machine-wide, and a clean removal on uninstall.
/// </summary>
public static class NativeHostRegistrar
{
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public static string DefaultManifestDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaDownload", "NativeMessaging");

    public static string ChromiumManifestPath(string? manifestDirectory = null) =>
        Path.Combine(manifestDirectory ?? DefaultManifestDirectory, BrowserIntegrationInfo.HostName + ".json");

    public static string FirefoxManifestPath(string? manifestDirectory = null) =>
        Path.Combine(manifestDirectory ?? DefaultManifestDirectory, BrowserIntegrationInfo.HostName + ".firefox.json");

    public static string BuildChromiumManifest(string hostExecutablePath, string chromiumExtensionId) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = BrowserIntegrationInfo.HostName,
            ["description"] = "NovaDownload browser integration host",
            ["path"] = hostExecutablePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{chromiumExtensionId}/" }
        }, ManifestJson);

    public static string BuildFirefoxManifest(string hostExecutablePath, string firefoxExtensionId) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = BrowserIntegrationInfo.HostName,
            ["description"] = "NovaDownload browser integration host",
            ["path"] = hostExecutablePath,
            ["type"] = "stdio",
            ["allowed_extensions"] = new[] { firefoxExtensionId }
        }, ManifestJson);

    /// <summary>Registers the host for every known browser. Returns what actually happened.</summary>
    public static RegistrationReport Register(string hostExecutablePath, string? manifestDirectory = null,
        string? chromiumExtensionId = null, string? firefoxExtensionId = null)
    {
        if (string.IsNullOrWhiteSpace(hostExecutablePath))
            throw new ArgumentException("Host executable path is required.", nameof(hostExecutablePath));
        if (!File.Exists(hostExecutablePath))
            throw new FileNotFoundException("Native messaging host executable not found.", hostExecutablePath);

        manifestDirectory ??= DefaultManifestDirectory;
        chromiumExtensionId ??= BrowserIntegrationInfo.ChromiumExtensionId;
        firefoxExtensionId ??= BrowserIntegrationInfo.FirefoxExtensionId;

        if (!ChromiumExtensionId.LooksValid(chromiumExtensionId))
            throw new ArgumentException($"'{chromiumExtensionId}' is not a valid Chromium extension id.", nameof(chromiumExtensionId));

        Directory.CreateDirectory(manifestDirectory);

        var chromiumManifest = ChromiumManifestPath(manifestDirectory);
        var firefoxManifest = FirefoxManifestPath(manifestDirectory);

        File.WriteAllText(chromiumManifest, BuildChromiumManifest(hostExecutablePath, chromiumExtensionId));
        File.WriteAllText(firefoxManifest, BuildFirefoxManifest(hostExecutablePath, firefoxExtensionId));

        var entries = new List<BrowserRegistration>();
        foreach (var target in BrowserCatalog.Targets)
        {
            var executable = BrowserCatalog.FindExecutable(target);
            var version = BrowserCatalog.TryGetVersion(executable);
            var manifest = target.Kind == BrowserKind.Firefox ? firefoxManifest : chromiumManifest;
            var detail = executable == null
                ? "Browser not detected on this machine — registered anyway for a later install."
                : $"Registered for {version ?? "unknown version"}.";

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(target.RegistryKey, writable: true);
                key?.SetValue(null, manifest, RegistryValueKind.String);
                entries.Add(new BrowserRegistration(target.Kind, target.DisplayName, executable ?? string.Empty, version,
                    manifest, target.RegistryKey, true, detail));
            }
            catch (Exception ex)
            {
                entries.Add(new BrowserRegistration(target.Kind, target.DisplayName, executable ?? string.Empty, version,
                    manifest, target.RegistryKey, false, $"Registration failed: {ex.Message}"));
            }
        }

        return new RegistrationReport(entries, manifestDirectory, hostExecutablePath);
    }

    /// <summary>Removes registry keys and manifests. Manifests are best-effort deleted.</summary>
    public static RegistrationReport Unregister(string? manifestDirectory = null)
    {
        manifestDirectory ??= DefaultManifestDirectory;
        var entries = new List<BrowserRegistration>();
        var chromiumManifest = ChromiumManifestPath(manifestDirectory);
        var firefoxManifest = FirefoxManifestPath(manifestDirectory);

        foreach (var target in BrowserCatalog.Targets)
        {
            var executable = BrowserCatalog.FindExecutable(target);
            var manifest = target.Kind == BrowserKind.Firefox ? firefoxManifest : chromiumManifest;
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(target.RegistryKey, throwOnMissingSubKey: false);
                entries.Add(new BrowserRegistration(target.Kind, target.DisplayName, executable ?? string.Empty, null,
                    manifest, target.RegistryKey, false, "Native messaging registration removed."));
            }
            catch (Exception ex)
            {
                entries.Add(new BrowserRegistration(target.Kind, target.DisplayName, executable ?? string.Empty, null,
                    manifest, target.RegistryKey, true, $"Removal failed: {ex.Message}"));
            }
        }

        foreach (var manifest in new[] { chromiumManifest, firefoxManifest })
        {
            try { if (File.Exists(manifest)) File.Delete(manifest); } catch { }
        }

        return new RegistrationReport(entries, manifestDirectory, string.Empty);
    }

    /// <summary>Current registration state without changing anything.</summary>
    public static RegistrationReport Inspect(string? manifestDirectory = null, string? hostExecutablePath = null)
    {
        manifestDirectory ??= DefaultManifestDirectory;
        var entries = new List<BrowserRegistration>();

        foreach (var target in BrowserCatalog.Targets)
        {
            var executable = BrowserCatalog.FindExecutable(target);
            var version = BrowserCatalog.TryGetVersion(executable);
            var manifest = target.Kind == BrowserKind.Firefox
                ? FirefoxManifestPath(manifestDirectory)
                : ChromiumManifestPath(manifestDirectory);
            var registeredPath = BrowserCatalog.ReadRegisteredManifest(target.RegistryKey);
            var registered = !string.IsNullOrEmpty(registeredPath) && File.Exists(registeredPath);
            var detail = registeredPath == null
                ? "Not registered."
                : registered
                    ? "Registered."
                    : $"Registry points at a missing manifest: {registeredPath}";

            entries.Add(new BrowserRegistration(target.Kind, target.DisplayName, executable ?? string.Empty, version,
                manifest, target.RegistryKey, registered, detail));
        }

        return new RegistrationReport(entries, manifestDirectory, hostExecutablePath ?? string.Empty);
    }
}
