using System.Windows;
using System.Windows.Input;

namespace NovaDownload.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionRun.Text = version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
