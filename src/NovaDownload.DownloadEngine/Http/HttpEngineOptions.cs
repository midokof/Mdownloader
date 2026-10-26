// language: C#, file: HttpEngineOptions.cs, target: .NET 9
namespace NovaDownload.DownloadEngine.Http;

/// <summary>How the engine reaches the network.</summary>
public enum HttpProxyMode
{
    /// <summary>Ignore every proxy, including the one Windows advertises.</summary>
    Direct,

    /// <summary>Use whatever the machine is configured with.</summary>
    System,

    /// <summary>Use <see cref="HttpEngineOptions.ProxyHost"/> and <see cref="HttpEngineOptions.ProxyPort"/>.</summary>
    Manual
}

/// <summary>
/// Live configuration of the HTTP engine. The service reads these fields when a transfer starts and
/// the limiter reads its budget on every chunk, so a change applies without rebuilding the service.
/// </summary>
public sealed class HttpEngineOptions
{
    /// <summary>Parallel range requests per transfer.</summary>
    public int Segments { get; set; } = 8;

    /// <summary>
    /// Smallest piece worth cutting. A 900 KiB file is served by one request even when the server
    /// accepts ranges, because the probe and the segment bookkeeping cost more than they save.
    /// </summary>
    public long MinimumSegmentBytes { get; set; } = 1024 * 1024;

    /// <summary>Attempts after the first one, spaced by exponential backoff.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Base of the exponential backoff: attempt n waits base × 2^(n-1), capped at 8 s.</summary>
    public int RetryBaseDelayMs { get; set; } = 400;

    /// <summary>Total download budget in bytes per second, across every transfer; 0 = unlimited.</summary>
    public long GlobalSpeedLimitBps { get; set; }

    public HttpProxyMode ProxyMode { get; set; } = HttpProxyMode.Direct;

    public string? ProxyHost { get; set; }

    public int ProxyPort { get; set; }

    /// <summary>Folder holding one JSON resume file per transfer.</summary>
    public string StateDirectory { get; set; } = DefaultStateDirectory();

    /// <summary>
    /// <c>%LOCALAPPDATA%\NovaDownload\http-state</c>, or the folder named by
    /// <c>NOVADOWNLOAD_DATA_DIR</c> when the environment forces one (tests, portable runs).
    /// </summary>
    public static string DefaultStateDirectory()
    {
        var root = Environment.GetEnvironmentVariable("NOVADOWNLOAD_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaDownload");

        return Path.Combine(root, "http-state");
    }
}
