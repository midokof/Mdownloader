using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaDownload.App.Services;
using NovaDownload.Core.Common;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

public partial class HistoryViewModel : LocalizedViewModel
{
    private readonly DownloadStore _store;
    private readonly IDownloadService _service;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private int completedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private string totalDownloadedText = string.Empty;

    public HistoryViewModel(DownloadStore store, IDownloadService service)
    {
        _store = store;
        _service = service;
        _store.Refreshed += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        var finished = _store.Items
            .Where(i => i.IsFinished)
            .OrderByDescending(i => i.UpdatedAt)
            .ToArray();

        DownloadStore.SyncCollection(Items, finished);

        CompletedCount = finished.Count(i => i.Status == NovaDownload.Core.Enums.DownloadStatus.Completed);
        FailedCount = finished.Count(i => i.Status == NovaDownload.Core.Enums.DownloadStatus.Failed);
        TotalDownloadedText = Formatters.Bytes(finished
            .Where(i => i.Status == NovaDownload.Core.Enums.DownloadStatus.Completed)
            .Sum(i => i.FileSize));
        IsEmpty = Items.Count == 0;
    }

    protected override void OnLanguageChanged() => Update();

    [RelayCommand]
    private void OpenFolder(DownloadItem? item)
    {
        if (item == null) return;
        ShellHelper.OpenFile(item.DestinationPath);
    }

    [RelayCommand]
    private async Task Clear()
    {
        foreach (var item in Items.ToArray())
            await _service.RemoveAsync(item.Id);
    }
}
