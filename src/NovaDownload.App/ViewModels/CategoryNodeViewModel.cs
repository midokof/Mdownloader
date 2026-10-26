using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// One line in the category pane. A node either carries a catalog key (built-in scopes) or a
/// literal label (a category the user created with "+"), and keeps its child nodes itself so the
/// tree can be rebuilt when a custom category appears.
/// </summary>
public partial class CategoryNodeViewModel : ObservableObject
{
    private readonly string? _labelKey;
    private readonly string _literal;

    public CategoryNodeViewModel(string tag, string iconKey, string? labelKey = null, string? literal = null)
    {
        Tag = tag;
        _labelKey = labelKey;
        _literal = literal ?? string.Empty;
        Icon = Application.Current?.TryFindResource(iconKey) as Geometry;

        Localizer.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    /// <summary>Value handed to <see cref="DownloadsViewModel.SelectScope"/>.</summary>
    public string Tag { get; }

    /// <summary>Path geometry of the little glyph in front of the label.</summary>
    public Geometry? Icon { get; }

    public ObservableCollection<CategoryNodeViewModel> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;

    public string Label => _labelKey != null ? Localizer.Get(_labelKey) : _literal;

    public CategoryNodeViewModel Add(CategoryNodeViewModel child)
    {
        Children.Add(child);
        OnPropertyChanged(nameof(HasChildren));
        return child;
    }
}
