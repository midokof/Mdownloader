using System.IO;
using NovaDownload.BrowserIntegration.Registration;

namespace NovaDownload.App.Services;

/// <summary>
/// Finds <c>NovaDownload.NativeHost.exe</c>. The installer and the release build place it next to
/// the application; a development layout can point at a build output through an environment variable.
/// </summary>
public static class NativeHostLocator
{
    public const string HostExecutableName = "NovaDownload.NativeHost.exe";
    public const string HostPathVariable = "NOVADOWNLOAD_NATIVE_HOST_PATH";

    public static string? TryResolve(out string source)
    {
        var forced = Environment.GetEnvironmentVariable(HostPathVariable);
        if (!string.IsNullOrWhiteSpace(forced) && File.Exists(forced))
        {
            source = $"{HostPathVariable} environment variable";
            return forced;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, HostExecutableName);
        if (File.Exists(beside))
        {
            source = "application folder";
            return beside;
        }

        var hostFolder = Path.Combine(AppContext.BaseDirectory, "host", HostExecutableName);
        if (File.Exists(hostFolder))
        {
            source = "application folder (host subdirectory)";
            return hostFolder;
        }

        foreach (var candidate in DevelopmentCandidates())
        {
            if (File.Exists(candidate))
            {
                source = "development build output";
                return candidate;
            }
        }

        source = "not found";
        return null;
    }

    private static IEnumerable<string> DevelopmentCandidates()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
        {
            var src = Path.Combine(directory.FullName, "src", "NovaDownload.NativeHost");
            if (!Directory.Exists(src)) continue;

            foreach (var configuration in new[] { "Release", "Debug" })
            {
                yield return Path.Combine(src, "bin", configuration, "net9.0-windows", HostExecutableName);
                yield return Path.Combine(src, "bin", configuration, "net9.0", HostExecutableName);
            }
        }
    }
}
