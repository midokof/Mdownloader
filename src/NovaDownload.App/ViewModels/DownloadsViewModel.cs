using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaDownload.App.Services;
using NovaDownload.App.Views;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// The central list: what it shows (category scope + search), what it has selected, and every
/// gesture a row accepts. One predicate drives the view and the F3 walk, so they agree.
/// </summary>
public partial class DownloadsViewModel : LocalizedViewModel
{
    private readonly DownloadStore _store;
    private readonly IDownloadService _service;
    private readonly Dictionary<Guid, DownloadStatus> _statuses = new();
    private int _matchIndex = -1;

    public ObservableCollection<DownloadItem> Items => _store.Items;

    public ICollectionView View { get; }

    /// <summary>Rows highlighted in the list; kept in sync by the view.</summary>
    public ObservableCollection<DownloadItem> Selection { get; } = new();

    /// <summary>Raised when the row to scroll into view changed (F3, select all).</summary>
    public event EventHandler<DownloadItem>? ScrollToRequested;

    /// <summary>Raised after a remove, so the host can drop the row from the archive too.</summary>
    public event EventHandler<Guid>? RowForgotten;

    [ObservableProperty] private DownloadScope scope = DownloadScope.All;
    [ObservableProperty] private DownloadCategory category = DownloadCategory.Other;

    /// <summary>Name of the user-created category when the pane sits on one of them.</summary>
    [ObservableProperty] private string customCategory = string.Empty;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private DownloadItem? selectedItem;
    [ObservableProperty] private bool hasSelection;
    [ObservableProperty] private string countsText = string.Empty;
    [ObservableProperty] private string globalSpeedText = string.Empty;
    [ObservableProperty] private string overallPercentText = string.Empty;
    [ObservableProperty] private double overallProgress;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string summaryText = string.Empty;

    public DownloadsViewModel(DownloadStore store, IDownloadService service)
    {
        _store = store;
        _service = service;

        View = CollectionViewSource.GetDefaultView(_store.Items);
        View.Filter = FilterItem;

        _store.Refreshed += (_, _) =>
        {
            // Rows move between scopes only when their status changes; a refresh on every tick
            // would rebuild the list four times a second for nothing.
            var moved = false;
            foreach (var item in _store.Items)
            {
                if (_statuses.TryGetValue(item.Id, out var before) && before == item.Status) continue;
                _statuses[item.Id] = item.Status;
                moved = true;
            }

            if (moved) View.Refresh();
            UpdateCounters();
            ResumeCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
        };

        UpdateCounters();

        Selection.CollectionChanged += (_, _) =>
        {
            ResumeCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
        };
    }

    private bool FilterItem(object candidate) =>
        candidate is DownloadItem item &&
        DownloadFilter.Matches(item, Scope, Category, SearchText, CustomCategory);

    // ---- scope and search ------------------------------------------------------

    /// <summary>
    /// Called by the category pane. The tag is the contract between the tree and the predicate:
    /// <c>All</c>, <c>Compressed</c>… <c>Torrents</c>, <c>Unfinished</c>, <c>Finished</c>, <c>Queue</c>.
    /// </summary>
    public void SelectScope(string? tag)
    {
        if (tag is not null && tag.StartsWith("custom:", StringComparison.Ordinal))
        {
            CustomCategory = tag["custom:".Length..];
            Scope = DownloadScope.Custom;
            return;
        }

        switch (tag)
        {
            case "Unfinished":
                Scope = DownloadScope.Unfinished;
                break;
            case "Finished":
                Scope = DownloadScope.Finished;
                break;
            case "Queue":
                Scope = DownloadScope.Queue;
                break;
            case "Compressed":
            case "Documents":
            case "Music":
            case "Programs":
            case "Video":
            case "Torrents":
                Category = Enum.Parse<DownloadCategory>(tag);
                Scope = DownloadScope.Category;
                break;
            default:
                Scope = DownloadScope.All;
                break;
        }
    }

    partial void OnScopeChanged(DownloadScope value) => RefreshView();

    partial void OnCategoryChanged(DownloadCategory value) => RefreshView();

    partial void OnCustomCategoryChanged(string value) => RefreshView();

    partial void OnSearchTextChanged(string value)
    {
        _matchIndex = -1;
        OnPropertyChanged(nameof(HasSearchText));
        RefreshView();
    }

    /// <summary>False when the box is empty: the view shows its placeholder on that.</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    private void RefreshView()
    {
        View.Refresh();
        UpdateCounters();
    }

    /// <summary>F5 and the end of a dialog: counters and membership are recomputed in place.</summary>
    public void Refresh()
    {
        _statuses.Clear();
        foreach (var item in _store.Items) _statuses[item.Id] = item.Status;

        View.Refresh();
        UpdateCounters();
    }

    /// <summary>F3: walks the visible rows, wrapping, and asks the view to bring the match into sight.</summary>
    public bool FindNext()
    {
        var matches = View.Cast<DownloadItem>().ToList();
        if (matches.Count == 0) return false;

        _matchIndex = (_matchIndex + 1) % matches.Count;
        SelectedItem = matches[_matchIndex];
        ScrollToRequested?.Invoke(this, SelectedItem);
        return true;
    }

    // ---- counters --------------------------------------------------------------

    private void UpdateCounters()
    {
        var items = _store.Items;
        var active = items.Count(item => item.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        var paused = items.Count(item => item.Status == DownloadStatus.Paused);
        var done = items.Count(item => item.Status == DownloadStatus.Completed);

        CountsText = Localizer.Format("Status_Counts", items.Count, active, paused, done);

        var speed = items.Where(item => item.Status == DownloadStatus.Downloading).Sum(item => item.SpeedBps);
        GlobalSpeedText = Localizer.Format("Status_GlobalSpeed", Formatters.Speed(speed));

        var running = items.Where(item => item.Status == DownloadStatus.Downloading).ToArray();
        var total = running.Sum(item => Math.Max(item.FileSize, item.ProgressBytes));
        OverallProgress = total > 0
            ? Math.Clamp(running.Sum(item => item.ProgressBytes) * 100.0 / total, 0, 100)
            : 0;

        OverallPercentText = Localizer.Format("Status_Overall", OverallProgress);

        var visible = View.Cast<object>().Count();
        IsEmpty = visible == 0;
        SummaryText = Localizer.Format("List_Summary", visible, items.Count);

        Raise(nameof(QueueHintText));
    }

    /// <summary>"3 en file d'attente" — only shown when the queue actually has rows.</summary>
    public string QueueHintText
    {
        get
        {
            var waiting = _store.Items.Count(item => item.Status == DownloadStatus.Queued);
            return waiting == 0 ? string.Empty : Localizer.Format("Status_QueuedCount", waiting);
        }
    }

    protected override void OnLanguageChanged() => UpdateCounters();

    // ---- selection -------------------------------------------------------------

    public void SetSelection(IList<DownloadItem> rows)
    {
        Selection.Clear();
        foreach (var row in rows) Selection.Add(row);

        HasSelection = Selection.Count > 0;
        SelectedItem = Selection.Count > 0 ? Selection[^1] : null;
    }

    /// <summary>Right-click: the row under the cursor becomes the selection, so the menu acts on it.</summary>
    public void SelectSingle(DownloadItem? item)
    {
        if (item == null) return;

        Selection.Clear();
        Selection.Add(item);
        SelectedItem = item;
        HasSelection = true;
    }

    private IReadOnlyList<DownloadItem> Targets =>
        Selection.Count > 0 ? Selection.ToArray() : SelectedItem is { } one ? new[] { one } : Array.Empty<DownloadItem>();

    // ---- row commands ----------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task Pause()
    {
        foreach (var item in Targets) await _service.PauseAsync(item.Id);
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task Resume()
    {
        foreach (var item in Targets) await _service.ResumeAsync(item.Id);
    }

    private bool CanPause() => Targets.Any(i =>
        i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Queued);

    private bool CanResume() => Targets.Any(i => i.Status == DownloadStatus.Paused);

    partial void OnSelectedItemChanged(DownloadItem? value)
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Restart()
    {
        foreach (var item in Targets) await _service.RestartAsync(item.Id);
    }

    [RelayCommand]
    private async Task Remove()
    {
        await RemoveTargets(deleteFiles: false);
    }

    /// <summary>
    /// Delete with the payload. The confirmation is the only place where a click removes bytes the
    /// user cannot get back without downloading them again.
    /// </summary>
    [RelayCommand]
    private async Task RemoveWithFiles()
    {
        var targets = Targets;
        if (targets.Count == 0) return;

        var answer = MessageBox.Show(
            Localizer.Format("Dlg_DeleteFilesOne", targets[0].FileName),
            Localizer.Get("Dlg_DeleteFilesTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        await RemoveTargets(deleteFiles: true);
    }

    private async Task RemoveTargets(bool deleteFiles)
    {
        var targets = Targets;
        if (targets.Count == 0) return;

        // A row still holding resume data loses it; the user asked for the row to go.
        var unfinished = targets.Count(item => !item.IsFinished);
        if (unfinished > 0)
        {
            var answer = MessageBox.Show(
                Localizer.Format("Dlg_RemoveRunning", unfinished),
                Localizer.Get("Dlg_RemoveTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        foreach (var item in targets)
        {
            await _service.RemoveAsync(item.Id, deleteFiles);
            _store.Forget(item.Id);
            RowForgotten?.Invoke(this, item.Id);
        }
    }

    /// <summary>Drops every finished row: the engine forgets it, the archive too.</summary>
    [RelayCommand]
    private async Task ClearFinished()
    {
        foreach (var item in _store.Items.Where(row => row.IsFinished).ToArray())
        {
            await _service.RemoveAsync(item.Id);
            _store.Forget(item.Id);
            RowForgotten?.Invoke(this, item.Id);
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var item = SelectedItem ?? Targets.FirstOrDefault();
        if (item == null) return;

        if (!string.IsNullOrEmpty(item.DestinationPath) && System.IO.File.Exists(item.DestinationPath))
            ShellHelper.OpenFile(item.DestinationPath);
        else
            ShellHelper.OpenFolder(item.Folder);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var item = SelectedItem ?? Targets.FirstOrDefault();
        if (item == null) return;
        ShellHelper.OpenFolder(item.Folder);
    }

    [RelayCommand]
    private void CopyUrl()
    {
        var item = SelectedItem ?? Targets.FirstOrDefault();
        if (item == null || string.IsNullOrEmpty(item.Url)) return;

        try { Clipboard.SetText(item.Url); }
        catch (Exception) { }
    }

    /// <summary>
    /// Re-opens the file picker for a live multi-file torrent. A magnet only knows its files once
    /// a peer answered, so this is the point where the selection becomes possible.
    /// </summary>
    [RelayCommand]
    private async Task ChooseFiles()
    {
        var item = SelectedItem ?? Targets.FirstOrDefault();
        if (item == null || item.TorrentFiles.Count <= 1) return;

        if (!TorrentFilesWindow.Ask(item.FileName, item.TorrentFiles.ToArray(), item.Folder,
                showDestination: false, out _, out var selection))
            return;

        await _service.SelectFilesAsync(item.Id, selection);
    }

    // ---- queue-wide commands ---------------------------------------------------

    [RelayCommand]
    private async Task PauseAll() => await _service.PauseAllAsync();

    [RelayCommand]
    private async Task ResumeAll() => await _service.ResumeAllAsync();

    [RelayCommand]
    private async Task StopAll() => await _service.CancelAllAsync();

    // ---- sorting ---------------------------------------------------------------

    /// <summary>Header click: sorts the visible rows by the named property.</summary>
    public void SortBy(string property, ListSortDirection direction)
    {
        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(new SortDescription(property, direction));
    }
}
