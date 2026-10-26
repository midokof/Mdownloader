using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaDownload.App.ViewModels;
using NovaDownload.Core.Models;

namespace NovaDownload.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// True while the window is parked in the tray because it was minimised. The taskbar preview,
    /// Win+Up and Alt+Tab can bring the HWND back without going through the tray icon; without this
    /// flag the window would reappear empty, since WPF still holds Visibility=Hidden.
    /// </summary>
    private bool _hiddenByMinimize;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        StateChanged += (_, _) => UpdateChrome();

        viewModel.ShowWindowRequested += (_, _) => RestoreFromTray();
        viewModel.FocusSearchRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
        viewModel.SelectAllRequested += (_, _) => DownloadsList.SelectAll();
        viewModel.Downloads.ScrollToRequested += (_, item) => DownloadsList.ScrollIntoView(item);

        Loaded += (_, _) => SelectInitialCategory();

        // A header click sorts: the column tag names the property, the direction flips per click.
        DownloadsList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(ColumnHeader_Click));
    }

    // ---- custom chrome ---------------------------------------------------------

    private void UpdateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;

        // A borderless window maximised to the work area still overlaps it by the resize frame.
        RootBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);

        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(
            maximized ? "IconRestore" : "IconMaximize");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>Closing hides: the transfers keep running and the tray icon brings the window back.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.ExitRequested)
        {
            // The close button hides: transfers keep running and the tray brings the window back.
            // Quitting happens from the tray menu or Tâches → Quitter, with a confirmation.
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.FlushArchive();
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            // Options decides whether "Réduire" parks the window in the tray (no taskbar entry)
            // or leaves it in the taskbar like an ordinary window.
            if (!_viewModel.MinimizesToTray) return;

            _hiddenByMinimize = true;
            Hide();
        }
        else if (_hiddenByMinimize && !IsVisible)
        {
            // Restored from outside the tray (taskbar preview, Win+Up, Alt+Tab): show the real
            // window instead of leaving an empty frame on screen.
            _hiddenByMinimize = false;
            Show();
        }
    }

    private void RestoreFromTray()
    {
        _hiddenByMinimize = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ---- category pane ---------------------------------------------------------

    private void SelectInitialCategory()
    {
        var target = _viewModel.SelectedCategoryNode;
        if (target == null) return;

        var root = CategoryTree.ItemContainerGenerator.ContainerFromItem(_viewModel.Categories[0]) as TreeViewItem;
        if (root == null) return;

        if (_viewModel.Categories[0] == target)
        {
            root.IsSelected = true;
            return;
        }

        root.IsExpanded = true;
        if (root.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem child) child.IsSelected = true;
    }

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.SelectNode(e.NewValue as CategoryNodeViewModel);
    }

    // ---- list ------------------------------------------------------------------

    private void DownloadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rows = DownloadsList.SelectedItems.Cast<DownloadItem>().ToArray();
        _viewModel.Downloads.SetSelection(rows);
    }

    /// <summary>Right click acts on the row under the cursor, not on whatever was selected before.</summary>
    private void DownloadsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = ItemsControl.ContainerFromElement(DownloadsList, source) as ListViewItem;
        if (container?.DataContext is not DownloadItem item) return;

        if (!container.IsSelected)
        {
            DownloadsList.SelectedItems.Clear();
            container.IsSelected = true;
        }

        _viewModel.Downloads.SelectSingle(item);
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;

        // Columns have no Tag (GridViewColumn is not a framework element), so the order here is
        // the contract between the header clicked and the property sorted on.
        var grid = (GridView)DownloadsList.View;
        var index = grid.Columns.IndexOf(column);
        if (index < 0 || index >= SortProperties.Length) return;

        _sortDescending = ReferenceEquals(column, _sortColumn) ? !_sortDescending : false;
        _sortColumn = column;

        _viewModel.Downloads.SortBy(SortProperties[index],
            _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
    }

    private GridViewColumn? _sortColumn;
    private bool _sortDescending;

    private static readonly string[] SortProperties =
    {
        "FileName", "QueuePosition", "Category", "FileSize", "Status",
        "RemainingText", "SpeedBps", "LastTriedAt", "Description"
    };

    // ---- drag and drop ---------------------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                    e.Data.GetDataPresent(DataFormats.Text) ||
                    e.Data.GetDataPresent(DataFormats.UnicodeText)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var sources = new List<string>();

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            sources.AddRange(files.Where(file => file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)));
        }

        var text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
        if (!string.IsNullOrWhiteSpace(text)) sources.Add(text.Trim());

        if (sources.Count == 0) return;
        await _viewModel.AcceptDropAsync(sources);
    }
}
