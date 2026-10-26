using System.Windows;
using System.Windows.Input;
using NovaDownload.App.ViewModels;

namespace NovaDownload.App.Views;

public partial class PromptWindow : Window
{
    private readonly PromptViewModel _viewModel;

    public PromptWindow(PromptViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>One-field prompt. Returns the trimmed name, or null when the user backed out.</summary>
    public static string? Ask(string titleKey, string labelKey, Func<string, bool>? exists = null)
    {
        var viewModel = new PromptViewModel(titleKey, labelKey, null, exists);
        var window = new PromptWindow(viewModel);

        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible && !ReferenceEquals(owner, window)) window.Owner = owner;

        var confirmed = window.ShowDialog() == true;
        var value = viewModel.Value;
        viewModel.Dispose();
        return confirmed ? value : null;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Validate()) return;
        DialogResult = true;
    }
}
