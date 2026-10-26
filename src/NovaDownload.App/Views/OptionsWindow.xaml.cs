using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class OptionsWindow : Window
{
    private readonly OptionsViewModel _viewModel;

    public OptionsWindow(OptionsViewModel viewModel)
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

    /// <summary>Options are written by the view model; the dialog closes only after a real save.</summary>
    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveCommand.ExecuteAsync(null);
        DialogResult = true;
    }
}
