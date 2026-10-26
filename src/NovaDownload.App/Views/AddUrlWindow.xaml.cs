using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class AddUrlWindow : Window
{
    private readonly AddUrlViewModel _viewModel;

    public AddUrlWindow(AddUrlViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Validation lives in the view model; only a fully accepted batch closes the dialog.</summary>
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryAccept()) return;
        DialogResult = true;
    }
}
