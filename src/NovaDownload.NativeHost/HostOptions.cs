namespace NovaDownload.NativeHost;

/// <summary>Runtime settings of the native messaging host, all overridable from the command line.</summary>
public sealed class HostOptions
{
    /// <summary>Explicit path to NovaDownload.App.exe. Null lets <c>AppLocator</c> search.</summary>
    public string? AppPath { get; set; }

    /// <summary>Pipe name override — normally derived from the current user.</summary>
    public string? PipeName { get; set; }

    /// <summary>When false the host never starts the application; used by tests and diagnostics.</summary>
    public bool AllowLaunch { get; set; } = true;

    /// <summary>How long to wait for a freshly started application to answer on its pipe.</summary>
    public TimeSpan LaunchTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Directory receiving the browser native messaging manifests.</summary>
    public string? ManifestDirectory { get; set; }

    /// <summary>Mirror diagnostics to stderr (never stdout: that stream is the browser protocol).</summary>
    public bool Verbose { get; set; }

    /// <summary>Emit machine-readable JSON from the administrative modes (<c>--identity</c>, <c>--status</c>).</summary>
    public bool Json { get; set; }

    /// <summary>
    /// Origin argument the browser appends when it launches the host
    /// (<c>chrome-extension://&lt;id&gt;/</c>). Recorded for diagnostics only: Chromium has already
    /// checked it against the manifest's <c>allowed_origins</c> before starting the process.
    /// </summary>
    public string? CallerOrigin { get; set; }
}
