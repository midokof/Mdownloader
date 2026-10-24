namespace NovaDownload.Core.Models;

public class AppSettings
{
    public string DefaultDownloadDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int MaxConcurrentDownloads { get; set; } = 3;

    public int ConnectionsPerDownload { get; set; } = 4;

    public string Theme { get; set; } = "Light";

    /// <summary>"Auto" follows the Windows display language; "en" / "fr" pin one.</summary>
    public string Language { get; set; } = "Auto";

    public bool EnableBrowserIntegration { get; set; } = true;

    /// <summary>Restore and focus the window when a browser hands a download to NovaDownload.</summary>
    public bool ActivateOnBrowserDownload { get; set; } = true;

    public bool NotifyOnComplete { get; set; } = true;

    public bool MinimizeToTray { get; set; }
}
