using System.Windows;

namespace NovaDownload.App.Services;

/// <summary>
/// Swaps the colour dictionary at index 0 of the application resources.
/// Controls consume brushes through DynamicResource so the change is live.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

    public bool IsDark { get; private set; }

    public void Apply(string? theme)
    {
        IsDark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        var app = Application.Current;
        if (app == null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = IsDark ? DarkUri : LightUri };

        if (dictionaries.Count == 0) dictionaries.Add(replacement);
        else dictionaries[0] = replacement;
    }

    public string Toggle() => IsDark ? "Light" : "Dark";
}
