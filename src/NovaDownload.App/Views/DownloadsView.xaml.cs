using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NovaDownload.App.Services;
using NovaDownload.Core.Models;

namespace NovaDownload.App.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView() => InitializeComponent();

    private void DownloadList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var container = FindAncestor<ListBoxItem>(source);
        if (container?.DataContext is DownloadItem item)
            ShellHelper.OpenFile(item.DestinationPath);
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
