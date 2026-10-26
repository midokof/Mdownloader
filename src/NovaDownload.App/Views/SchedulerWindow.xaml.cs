using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class SchedulerWindow : Window
{
    private readonly SchedulerViewModel _viewModel;

    public SchedulerWindow(SchedulerViewModel viewModel)
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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Save()) return;
        DialogResult = true;
    }
}
