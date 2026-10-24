using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Localization;

namespace NovaDownload.Core.Models;

public partial class DownloadItem : ObservableObject
{
    [ObservableProperty]
    private Guid id = Guid.NewGuid();

    [ObservableProperty]
    private string url = string.Empty;

    [ObservableProperty]
    private string fileName = string.Empty;

    [ObservableProperty]
    private string destinationPath = string.Empty;

    [ObservableProperty]
    private long fileSize;

    [ObservableProperty]
    private long progressBytes;

    [ObservableProperty]
    private double speedBps;

    [ObservableProperty]
    private DownloadStatus status = DownloadStatus.Queued;

    [ObservableProperty]
    private bool supportsRange;

    [ObservableProperty]
    private string? eTag;

    [ObservableProperty]
    private string? lastModified;

    [ObservableProperty]
    private int retryCount;

    [ObservableProperty]
    private DateTime createdAt = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime updatedAt = DateTime.UtcNow;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private int connectionCount = 1;

    // ---- derived display values -------------------------------------------------

    public double ProgressPercent =>
        FileSize > 0
            ? Math.Clamp(ProgressBytes * 100.0 / FileSize, 0, 100)
            : (Status == DownloadStatus.Completed ? 100 : 0);

    public string SizeText => FileSize > 0 ? Formatters.Bytes(FileSize) : Localizer.Get("Common_Dash");

    public string ProgressText => FileSize > 0
        ? $"{Formatters.Bytes(ProgressBytes)} / {Formatters.Bytes(FileSize)}"
        : Formatters.Bytes(ProgressBytes);

    public string SpeedText => Status == DownloadStatus.Downloading
        ? Formatters.Speed(SpeedBps)
        : Localizer.Get("Common_Dash");

    public string EtaText => Status == DownloadStatus.Downloading
        ? Formatters.Eta(FileSize - ProgressBytes, SpeedBps)
        : Localizer.Get("Common_Dash");

    public string MetricPrimary => Status switch
    {
        DownloadStatus.Downloading => Formatters.Speed(SpeedBps),
        DownloadStatus.Queued => Localizer.Get("Metric_Waiting"),
        DownloadStatus.Paused => Localizer.Format("Metric_PausedAt", Formatters.TimeOfDay(UpdatedAt)),
        DownloadStatus.Completed => Localizer.Format("Metric_FinishedAt", Formatters.TimeOfDay(UpdatedAt)),
        DownloadStatus.Failed => Localizer.Get("Status_Failed"),
        DownloadStatus.Canceled => Localizer.Get("Status_Canceled"),
        _ => Localizer.Get("Common_Dash")
    };

    public string MetricSecondary => Status == DownloadStatus.Downloading
        ? Localizer.Format(
            "Metric_EtaWithSize",
            Formatters.Eta(FileSize - ProgressBytes, SpeedBps),
            Formatters.Bytes(FileSize))
        : Formatters.Bytes(FileSize > 0 ? FileSize : ProgressBytes);

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Localizer.Get("Status_Queued"),
        DownloadStatus.Downloading => Localizer.Get("Status_Downloading"),
        DownloadStatus.Paused => Localizer.Get("Status_Paused"),
        DownloadStatus.Completed => Localizer.Get("Status_Completed"),
        DownloadStatus.Failed => Localizer.Get("Status_Failed"),
        DownloadStatus.Canceled => Localizer.Get("Status_Canceled"),
        _ => Localizer.Get("Status_Unknown")
    };

    /// <summary>Re-reads every localized projection after the UI language changes.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(MetricPrimary));
        OnPropertyChanged(nameof(MetricSecondary));
        OnPropertyChanged(nameof(StatusText));
    }

    public string Folder => string.IsNullOrEmpty(DestinationPath)
        ? string.Empty
        : (System.IO.Path.GetDirectoryName(DestinationPath) ?? DestinationPath);

    public bool IsRunning => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued;

    public bool IsPaused => Status == DownloadStatus.Paused;

    public bool IsFinished =>
        Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    public bool IsOngoing => !IsFinished;

    public bool CanResume =>
        Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;

    public string Initial => string.IsNullOrEmpty(FileName) ? "?" : FileName[..1].ToUpperInvariant();

    public string Extension =>
        string.IsNullOrEmpty(FileName) ? string.Empty : System.IO.Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    partial void OnFileSizeChanged(long value) => RaiseMetrics();

    partial void OnProgressBytesChanged(long value) => RaiseMetrics();

    partial void OnSpeedBpsChanged(double value) => RaiseMetrics();

    partial void OnStatusChanged(DownloadStatus value) => RaiseMetrics();

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(Extension));
    }

    partial void OnDestinationPathChanged(string value) => OnPropertyChanged(nameof(Folder));

    partial void OnUpdatedAtChanged(DateTime value) => OnPropertyChanged(nameof(MetricPrimary));

    private void RaiseMetrics()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(MetricPrimary));
        OnPropertyChanged(nameof(MetricSecondary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsOngoing));
        OnPropertyChanged(nameof(CanResume));
    }
}
