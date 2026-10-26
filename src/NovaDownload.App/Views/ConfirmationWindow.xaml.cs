using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NovaDownload.App.ViewModels;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.Views;

public partial class ConfirmationWindow : Window
{
    private readonly ConfirmationViewModel _viewModel;

    public ConfirmationWindow(ConfirmationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>The real start: the engine is called here and only a success closes the dialog.</summary>
    private async void Start_Click(object sender, RoutedEventArgs e) => await CommitAsync(startLater: false);

    /// <summary>"Plus tard": the row is queued, then paused, so it holds its place without moving.</summary>
    private async void Later_Click(object sender, RoutedEventArgs e) => await CommitAsync(startLater: true);

    private async Task CommitAsync(bool startLater)
    {
        if (_viewModel.IsBusy) return;

        StartButton.IsEnabled = false;
        LaterButton.IsEnabled = false;

        try
        {
            _viewModel.StartLater = startLater;
            if (await _viewModel.CommitAsync()) DialogResult = true;
        }
        finally
        {
            StartButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _viewModel.Folder };
        if (dialog.ShowDialog() == true) _viewModel.Folder = dialog.FolderName;
    }

    /// <summary>The "+" of the category combo: a name becomes a pane node, immediately selected.</summary>
    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var existing = _viewModel.Categories
            .Where(option => option.Custom.Length > 0)
            .Select(option => option.Custom)
            .ToArray();

        var name = PromptWindow.Ask(
            Localizer.Get("Dlg_NewCategoryTitle"),
            Localizer.Get("Dlg_NewCategoryLabel"),
            candidate => existing.Contains(candidate, StringComparer.OrdinalIgnoreCase));

        if (name != null) _viewModel.AddCustomCategory(name);
    }
}
