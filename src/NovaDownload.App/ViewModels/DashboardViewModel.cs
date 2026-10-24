using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaDownload.App.Services;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

public partial class DashboardViewModel : LocalizedViewModel
{
    private readonly DownloadStore _store;
    private readonly ISettingsService _settings;
    private DateTime _lastDiskProbe = DateTime.MinValue;

    public event EventHandler? NewDownloadRequested;

    public ObservableCollection<DownloadItem> ActiveItems { get; } = new();
    public ObservableCollection<DownloadItem> RecentItems { get; } = new();

    [ObservableProperty] private int totalDownloads;
    [ObservableProperty] private int activeDownloads;
    [ObservableProperty] private int pausedDownloads;
    [ObservableProperty] private int completedDownloads;
    [ObservableProperty] private int failedDownloads;
    [ObservableProperty] private string totalSpeedText = string.Empty;
    [ObservableProperty] private double overallProgress;
    [ObservableProperty] private string freeSpaceText = string.Empty;
    [ObservableProperty] private string downloadFolder = string.Empty;
    [ObservableProperty] private bool hasActive;

    public DashboardViewModel(DownloadStore store, ISettingsService settings)
    {
        _store = store;
        _settings = settings;
        _store.Refreshed += (_, _) => Update();
        Update();
    }

    public string OverallProgressText => Localizer.Format("Dash_ProgressOfPayload", OverallProgress);

    public string OverallPercentText => Localizer.Format("Dash_Percent", OverallProgress);

    [RelayCommand]
    private void NewDownload() => NewDownloadRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenFolder() => ShellHelper.OpenFolder(DownloadFolder);

    private void Update()
    {
        var items = _store.Items;
        TotalDownloads = items.Count;
        ActiveDownloads = items.Count(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        PausedDownloads = items.Count(i => i.Status == DownloadStatus.Paused);
        CompletedDownloads = items.Count(i => i.Status == DownloadStatus.Completed);
        FailedDownloads = items.Count(i => i.Status == DownloadStatus.Failed);

        var speed = items.Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.SpeedBps);
        TotalSpeedText = Formatters.Speed(speed);

        var running = items.Where(i => i.Status == DownloadStatus.Downloading).ToArray();
        var totalBytes = running.Sum(i => Math.Max(i.FileSize, i.ProgressBytes));
        OverallProgress = totalBytes > 0 ? Math.Clamp(running.Sum(i => i.ProgressBytes) * 100.0 / totalBytes, 0, 100) : 0;

        DownloadStore.SyncCollection(ActiveItems, items
            .Where(i => !i.IsFinished)
            .OrderBy(i => i.Status == DownloadStatus.Paused ? 1 : 0)
            .ThenByDescending(i => i.CreatedAt)
            .Take(4)
            .ToArray());

        DownloadStore.SyncCollection(RecentItems, items
            .Where(i => i.IsFinished)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(4)
            .ToArray());

        HasActive = ActiveItems.Count > 0;
        DownloadFolder = _settings.Current.DefaultDownloadDirectory;

        if (DateTime.UtcNow - _lastDiskProbe > TimeSpan.FromSeconds(5)) ProbeDisk();
    }

    private void ProbeDisk()
    {
        _lastDiskProbe = DateTime.UtcNow;
        try
        {
            var root = Path.GetPathRoot(_settings.Current.DefaultDownloadDirectory);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            FreeSpaceText = drive.IsReady ? Formatters.Bytes(drive.AvailableFreeSpace) : Localizer.Get("Common_Dash");
        }
        catch
        {
            FreeSpaceText = Localizer.Get("Common_Dash");
        }
    }

    protected override void OnLanguageChanged()
    {
        _lastDiskProbe = DateTime.MinValue;
        Update();
        Raise(nameof(OverallProgressText), nameof(OverallPercentText));
    }

    partial void OnOverallProgressChanged(double value) =>
        Raise(nameof(OverallProgressText), nameof(OverallPercentText));

    public void RequestNewDownload() => NewDownloadRequested?.Invoke(this, EventArgs.Empty);
}
