using System.ComponentModel;
using System.Windows.Data;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.Services;

/// <summary>
/// Binding source for catalog strings. XAML binds through <c>{loc:Tr Key}</c>; one
/// notification refreshes every localized binding in the tree, so the language can be
/// switched without rebuilding views.
/// </summary>
public sealed class LocalizationProxy : INotifyPropertyChanged
{
    public static LocalizationProxy Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationProxy()
    {
        Localizer.LanguageChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        };
    }

    /// <summary>Resolved string for <paramref name="key"/>, following the active language.</summary>
    public string this[string key] => Localizer.Get(key);
}
