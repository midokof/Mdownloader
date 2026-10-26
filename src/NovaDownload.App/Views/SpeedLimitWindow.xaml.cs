using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class SpeedLimitWindow : Window
{
    public SpeedLimitWindow(SpeedLimitViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Saving only records the choice; the shell pushes it into the engines and closes.</summary>
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
