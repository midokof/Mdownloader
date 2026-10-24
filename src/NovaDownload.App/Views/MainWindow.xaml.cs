using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        StateChanged += (_, _) => UpdateMaxIcon();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { CommandParameter: string key }) return;

        switch (key)
        {
            case "Dashboard": _viewModel.ShowDashboardCommand.Execute(null); break;
            case "Downloads": _viewModel.ShowDownloadsCommand.Execute(null); break;
            case "AddDownload": _viewModel.ShowAddDownloadCommand.Execute(null); break;
            case "History": _viewModel.ShowHistoryCommand.Execute(null); break;
            case "Settings": _viewModel.ShowSettingsCommand.Execute(null); break;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<TextBox>(source) != null || FindAncestor<Button>(source) != null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaxIcon()
    {
        if (MaxIcon == null) return;
        MaxIcon.Data = (Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
