using System.Collections.ObjectModel;
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

    // ---- torrent-specific ------------------------------------------------------

    [ObservableProperty]
    private TransferKind kind = TransferKind.Http;

    /// <summary>Hex info hash, once a magnet or a .torrent has been parsed.</summary>
    [ObservableProperty]
    private string? infoHash;

    [ObservableProperty]
    private int seeds;

    [ObservableProperty]
    private int leechers;

    /// <summary>Peers currently connected to this torrent.</summary>
    [ObservableProperty]
    private int connectedPeers;

    /// <summary>Peers the tracker, DHT or peer exchange has announced but that are not connected yet.</summary>
    [ObservableProperty]
    private int availablePeers;

    /// <summary>Files in a multi-file torrent. Empty for single-file torrents and before metadata arrives.</summary>
    [ObservableProperty]
    private ObservableCollection<TorrentFileInfo> torrentFiles = new();

    [ObservableProperty]
    private double uploadSpeedBps;

    [ObservableProperty]
    private long uploadedBytes;

    /// <summary>Shared / downloaded, 0 until the first byte has been shared.</summary>
    [ObservableProperty]
    private double ratio;

    /// <summary>Raw swarm phase ("Metadata", "Hashing", "Seeding"…) while it differs from the queue status.</summary>
    [ObservableProperty]
    private string? phase;

    // ---- derived display values -------------------------------------------------

    public double ProgressPercent =>
        FileSize > 0
            ? Math.Clamp(ProgressBytes * 100.0 / FileSize, 0, 100)
            : (Status == DownloadStatus.Completed ? 100 : 0);

    public string SizeText => FileSize > 0 ? Formatters.Bytes(FileSize) : Localizer.Get("Common_Dash");

    public string ProgressText => FileSize > 0
        ? $"{Formatters.Bytes(ProgressBytes)} / {Formatters.Bytes(FileSize)}"
        : Formatters.Bytes(ProgressBytes);

    /// <summary>Whole-percent progress for the row: "42 %". Empty while the size is unknown.</summary>
    public string ProgressPercentText => FileSize > 0 || Status == DownloadStatus.Completed
        ? Localizer.Format("Metric_Percent", ProgressPercent)
        : string.Empty;

    public string SpeedText => Status == DownloadStatus.Downloading
        ? Formatters.Speed(SpeedBps)
        : Localizer.Get("Common_Dash");

    public string EtaText => Status == DownloadStatus.Downloading
        ? Formatters.Eta(FileSize - ProgressBytes, SpeedBps)
        : Localizer.Get("Common_Dash");

    public string MetricPrimary => IsTorrent && Status == DownloadStatus.Completed && UploadSpeedBps > 0
        ? Formatters.Speed(UploadSpeedBps)
        : Status switch
        {
            DownloadStatus.Downloading => Formatters.Speed(SpeedBps),
            DownloadStatus.Queued => Localizer.Get("Metric_Waiting"),
            DownloadStatus.Paused => Localizer.Format("Metric_PausedAt", Formatters.TimeOfDay(UpdatedAt)),
            DownloadStatus.Completed => Localizer.Format("Metric_FinishedAt", Formatters.TimeOfDay(UpdatedAt)),
            DownloadStatus.Failed => Localizer.Get("Status_Failed"),
            DownloadStatus.Canceled => Localizer.Get("Status_Canceled"),
            _ => Localizer.Get("Common_Dash")
        };

    public string MetricSecondary => IsTorrent
        ? Localizer.Format("Metric_TorrentSwarm",
            Localizer.Plural("Metric_SeedOne", "Metric_Seeds", Seeds, Seeds),
            Localizer.Plural("Metric_PeerOne", "Metric_Peers", Leechers, Leechers))
        : Status == DownloadStatus.Downloading
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
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(MetricPrimary));
        OnPropertyChanged(nameof(MetricSecondary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(DetailText));
    }

    public string Folder => string.IsNullOrEmpty(DestinationPath)
        ? string.Empty
        : (System.IO.Path.GetDirectoryName(DestinationPath) ?? DestinationPath);

    public bool IsRunning => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued;

    /// <summary>
    /// Pausable includes a completed torrent: it is still holding a swarm session open, and
    /// stopping the seed is the same gesture as pausing a transfer.
    /// </summary>
    public bool CanPause => IsRunning || (IsTorrent && Status == DownloadStatus.Completed);

    public bool IsPaused => Status == DownloadStatus.Paused;

    public bool IsFinished =>
        Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    public bool IsOngoing => !IsFinished;

    public bool CanResume =>
        Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;

    public string Initial => string.IsNullOrEmpty(FileName) ? "?" : FileName[..1].ToUpperInvariant();

    public bool IsTorrent => Kind == TransferKind.Torrent;

    /// <summary>True for a multi-file torrent where the user can pick individual files.</summary>
    public bool IsMultiFileTorrent => IsTorrent && TorrentFiles.Count > 1;

    /// <summary>Badge text for the file tile: "BT" for a torrent, the extension otherwise.</summary>
    public string Extension => IsTorrent
        ? "BT"
        : string.IsNullOrEmpty(FileName)
            ? string.Empty
            : System.IO.Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    public string UploadText => IsTorrent ? Formatters.Speed(UploadSpeedBps) : Localizer.Get("Common_Dash");

    public string RatioText => IsTorrent
        ? Ratio.ToString("0.00", Localizer.Culture)
        : Localizer.Get("Common_Dash");

    /// <summary>Swarm phase for the tooltip, so the row itself stays readable.</summary>
    public string DetailText
    {
        get
        {
            if (!IsTorrent) return DestinationPath;

            var parts = new List<string>(5);
            if (!string.IsNullOrEmpty(Phase)) parts.Add(Phase!);
            if (!string.IsNullOrEmpty(InfoHash)) parts.Add(InfoHash!);
            parts.Add(Localizer.Format("Metric_TorrentRatio", RatioText));
            parts.Add(Localizer.Format("Metric_TorrentUpload", UploadText));
            if (AvailablePeers > 0) parts.Add(Localizer.Format("Metric_TorrentKnown", AvailablePeers));
            return string.Join(" · ", parts);
        }
    }

    partial void OnFileSizeChanged(long value) => RaiseMetrics();

    partial void OnProgressBytesChanged(long value) => RaiseMetrics();

    partial void OnSpeedBpsChanged(double value) => RaiseMetrics();

    partial void OnStatusChanged(DownloadStatus value) => RaiseMetrics();

    partial void OnUploadSpeedBpsChanged(double value)
    {
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(MetricPrimary));
    }

    partial void OnRatioChanged(double value)
    {
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnSeedsChanged(int value) => OnPropertyChanged(nameof(MetricSecondary));

    partial void OnLeechersChanged(int value) => OnPropertyChanged(nameof(MetricSecondary));

    partial void OnAvailablePeersChanged(int value) => OnPropertyChanged(nameof(DetailText));

    partial void OnPhaseChanged(string? value) => OnPropertyChanged(nameof(DetailText));

    partial void OnInfoHashChanged(string? value) => OnPropertyChanged(nameof(DetailText));

    partial void OnKindChanged(TransferKind value)
    {
        OnPropertyChanged(nameof(IsTorrent));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(DetailText));
        RaiseMetrics();
    }

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
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(MetricPrimary));
        OnPropertyChanged(nameof(MetricSecondary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsOngoing));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(DetailText));
    }
}
