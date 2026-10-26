using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;
using NovaDownload.Core.Models;

namespace NovaDownload.App.Views;

/// <summary>
/// Modal picker for the files inside a torrent. Two callers: the add page, before a .torrent
/// starts, and the row action that re-selects files of a magnet once its metadata arrived.
/// </summary>
public partial class TorrentFilesWindow : Window
{
    private readonly TorrentSelectionViewModel _viewModel;

    public TorrentFilesWindow(TorrentSelectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanConfirm) return;
        DialogResult = true;
    }

    /// <summary>
    /// Shows the picker over the main window. Returns false when the user cancelled; on success
    /// the destination folder and the ticked files come back together, so nothing is read twice.
    /// </summary>
    public static bool Ask(string torrentName, IReadOnlyCollection<TorrentFileInfo> files,
        string destinationDirectory, bool showDestination,
        out string chosenDirectory, out IReadOnlyCollection<TorrentFileInfo> selection)
    {
        var viewModel = new TorrentSelectionViewModel(torrentName, files, destinationDirectory, showDestination);
        var window = new TorrentFilesWindow(viewModel);

        var owner = Application.Current?.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window) && owner.IsVisible) window.Owner = owner;

        var confirmed = window.ShowDialog() == true;
        chosenDirectory = viewModel.DestinationDirectory;
        selection = viewModel.Selection();
        return confirmed;
    }
}
