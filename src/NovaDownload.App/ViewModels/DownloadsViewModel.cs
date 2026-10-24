using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaDownload.App.Services;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

public partial class DownloadsViewModel : LocalizedViewModel
{
    private readonly DownloadStore _store;
    private readonly IDownloadService _service;

    public ObservableCollection<DownloadItem> Items => _store.Items;

    public ICollectionView View { get; }

    /// <summary>Chip labels: "{0} done" style, so word order and plural stay translatable.</summary>
    public string ActiveChipText => Localizer.Plural("Chip_ActiveOne", "Chip_Active", ActiveCount, ActiveCount);
    public string DoneChipText => Localizer.Plural("Chip_DoneOne", "Chip_Done", CompletedCount, CompletedCount);
    public string FailedChipText => Localizer.Plural("Chip_FailedOne", "Chip_Failed", FailedCount, FailedCount);

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private int activeCount;
    [ObservableProperty] private int completedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string summary = string.Empty;

    public DownloadsViewModel(DownloadStore store, IDownloadService service)
    {
        _store = store;
        _service = service;

        View = CollectionViewSource.GetDefaultView(_store.Items);
        View.Filter = FilterItem;

        _store.Refreshed += (_, _) => UpdateCounts();
        UpdateCounts();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not DownloadItem item) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();
        return item.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || item.Url.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || item.Folder.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        ActiveCount = _store.Items.Count(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        CompletedCount = _store.Items.Count(i => i.Status == DownloadStatus.Completed);
        FailedCount = _store.Items.Count(i => i.Status == DownloadStatus.Failed);

        var visible = View.Cast<object>().Count();
        IsEmpty = visible == 0;
        Summary = _store.Items.Count == 0
            ? Localizer.Get("Downloads_SummaryEmpty")
            : Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", _store.Items.Count,
                visible, _store.Items.Count);

        Raise(nameof(ActiveChipText), nameof(DoneChipText), nameof(FailedChipText));
    }

    protected override void OnLanguageChanged() => UpdateCounts();

    [RelayCommand]
    private async Task Pause(DownloadItem? item)
    {
        if (item == null) return;
        await _service.PauseAsync(item.Id);
    }

    [RelayCommand]
    private async Task Resume(DownloadItem? item)
    {
        if (item == null) return;
        await _service.ResumeAsync(item.Id);
    }

    [RelayCommand]
    private async Task Cancel(DownloadItem? item)
    {
        if (item == null) return;
        await _service.CancelAsync(item.Id);
    }

    [RelayCommand]
    private async Task Remove(DownloadItem? item)
    {
        if (item == null) return;
        await _service.RemoveAsync(item.Id);
    }

    [RelayCommand]
    private void OpenFolder(DownloadItem? item)
    {
        if (item == null) return;
        ShellHelper.OpenFile(item.DestinationPath);
    }

    [RelayCommand]
    private async Task PauseAll()
    {
        await _service.PauseAllAsync();
    }

    [RelayCommand]
    private async Task ResumeAll()
    {
        await _service.ResumeAllAsync();
    }

    [RelayCommand]
    private async Task ClearFinished()
    {
        foreach (var item in _store.Items.Where(i => i.IsFinished).ToArray())
            await _service.RemoveAsync(item.Id);
    }
}
