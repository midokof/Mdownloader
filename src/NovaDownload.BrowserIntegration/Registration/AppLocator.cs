using System.Diagnostics;
using Microsoft.Win32;
using NovaDownload.BrowserIntegration.Protocol;

namespace NovaDownload.BrowserIntegration.Registration;

/// <summary>
/// Finds and starts the desktop application when the browser asks for a download and the app
/// is not running. The application is started with <c>ShellExecute</c> so the child process does
/// not inherit the native messaging stdio handles owned by the host.
/// </summary>
public static class AppLocator
{
    public const string AppExecutableName = "NovaDownload.App.exe";
    public const string AppPathVariable = "NOVADOWNLOAD_APP_PATH";

    public static string? TryResolve(out string source)
    {
        var forced = Environment.GetEnvironmentVariable(AppPathVariable);
        if (!string.IsNullOrWhiteSpace(forced) && File.Exists(forced))
        {
            source = $"{AppPathVariable} environment variable";
            return forced;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, AppExecutableName);
        if (File.Exists(beside))
        {
            source = "application folder";
            return beside;
        }

        var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", AppExecutableName));
        if (File.Exists(parent))
        {
            source = "parent of the host folder";
            return parent;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{BrowserIntegrationInfo.InstallerUninstallKey}");
                if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                {
                    var candidate = Path.Combine(location, AppExecutableName);
                    if (File.Exists(candidate))
                    {
                        source = "uninstall registry entry";
                        return candidate;
                    }
                }
            }
            catch
            {
                // Registry probing is best effort.
            }
        }

        foreach (var folder in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NovaDownload"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NovaDownload"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NovaDownload")
                 })
        {
            var candidate = Path.Combine(folder, AppExecutableName);
            if (File.Exists(candidate))
            {
                source = folder;
                return candidate;
            }
        }

        source = "not found";
        return null;
    }

    /// <summary>Starts the application and waits for its bridge pipe to answer.</summary>
    public static async Task<bool> TryStartAndWaitAsync(TimeSpan? timeout = null, string? pipeName = null,
        CancellationToken cancellationToken = default)
    {
        if (await BrowserBridgeProtocol.IsServerAvailableAsync(pipeName, TimeSpan.FromMilliseconds(400), cancellationToken)
                .ConfigureAwait(false))
            return true;

        var executable = TryResolve(out _);
        if (executable == null) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            });
        }
        catch
        {
            return false;
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            if (await BrowserBridgeProtocol.IsServerAvailableAsync(pipeName, TimeSpan.FromMilliseconds(400), cancellationToken)
                    .ConfigureAwait(false))
                return true;
        }

        return false;
    }
}
