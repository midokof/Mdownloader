using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.Core.Common;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// Backs the torrent file picker. The same view model serves both entry points: choosing files
/// before a .torrent starts, and changing the selection of a magnet once its metadata arrived.
/// </summary>
public partial class TorrentSelectionViewModel : LocalizedViewModel
{
    private readonly bool _showDestination;

    public TorrentSelectionViewModel(string torrentName, IEnumerable<TorrentFileInfo> files,
        string destinationDirectory, bool showDestination)
    {
        TorrentName = torrentName;
        DestinationDirectory = destinationDirectory;
        _showDestination = showDestination;

        Files = new ObservableCollection<TorrentFileViewModel>(files.Select(f => new TorrentFileViewModel(f)));
        foreach (var file in Files) file.PropertyChanged += OnFileChanged;
    }

    public ObservableCollection<TorrentFileViewModel> Files { get; }

    public string TorrentName { get; }

    /// <summary>True when the folder can still be chosen (before the transfer starts).</summary>
    public bool ShowDestination => _showDestination;

    [ObservableProperty]
    private string destinationDirectory = string.Empty;

    /// <summary>"3 files of 5 · 1,2 Go": what confirming would actually fetch.</summary>
    public string Summary
    {
        get
        {
            var selected = Files.Where(f => f.IsSelected).ToArray();
            var bytes = selected.Sum(f => f.Length);
            return Localizer.Plural("Torrent_SelectionOne", "Torrent_Selection", selected.Length,
                selected.Length, Files.Count, Formatters.Bytes(bytes));
        }
    }

    public bool CanConfirm => Files.Any(f => f.IsSelected);

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TorrentFileViewModel.IsSelected)) return;
        Raise(nameof(Summary), nameof(CanConfirm));
    }

    protected override void OnLanguageChanged() => Raise(nameof(Summary));

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var file in Files) file.IsSelected = selected;
        Raise(nameof(Summary), nameof(CanConfirm));
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DestinationDirectory };
        if (dialog.ShowDialog() == true) DestinationDirectory = dialog.FolderName;
    }

    /// <summary>The models themselves, so the engine reads the same flags the user ticked.</summary>
    public IReadOnlyCollection<TorrentFileInfo> Selection() => Files.Select(f => f.Model).ToArray();
}
