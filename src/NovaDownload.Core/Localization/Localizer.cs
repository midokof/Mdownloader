using System.Globalization;

namespace NovaDownload.Core.Localization;

/// <summary>
/// Resolves the UI language from the Windows display language unless the user pinned one,
/// and exposes the string catalog plus number/date culture for the whole app.
/// </summary>
public static class Localizer
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Catalogs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = LocaleCatalog.En,
        ["fr"] = LocaleCatalog.Fr
    };

    /// <summary>Windows UI language, captured before any override is applied.</summary>
    public static CultureInfo OsCulture { get; } = CultureInfo.CurrentUICulture;

    private static CultureInfo _culture = CultureInfo.CurrentUICulture;
    private static IReadOnlyDictionary<string, string> _active = LocaleCatalog.En;

    /// <summary>
    /// Establishes a coherent default before anyone calls <see cref="Apply"/>: catalog and
    /// number culture both follow the Windows display language.
    /// </summary>
    static Localizer() => Apply("Auto");

    /// <summary>Preference token: "Auto", "en" or "fr".</summary>
    public static string Preference { get; private set; } = "Auto";

    /// <summary>Two-letter language actually in use: "en" or "fr".</summary>
    public static string Language { get; private set; } = "en";

    public static CultureInfo Culture => _culture;

    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Raw string catalog for one language (<c>"en"</c> or <c>"fr"</c>); the active language when
    /// no code is given. Read-only and stable: used by diagnostics and by the parity tests.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Catalog(string? language = null)
    {
        var code = string.IsNullOrWhiteSpace(language) ? Language : language.Trim().ToLowerInvariant();
        return Catalogs.TryGetValue(code, out var catalog) ? catalog : Catalogs["en"];
    }

    public static string Get(string key)
    {
        if (_active.TryGetValue(key, out var value)) return value;
        return LocaleCatalog.En.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(_culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// Picks between two catalog forms for <paramref name="count"/>. The rule lives with the
    /// language: French counts 0 and 1 as singular, English only 1.
    /// </summary>
    public static string Plural(string keyOne, string keyMany, long count, params object?[] args)
        => Format(IsSingular(count) ? keyOne : keyMany, args);

    private static bool IsSingular(long count) => Language == "fr" ? count is 0 or 1 : count == 1;

    /// <summary>
    /// Applies a language preference. "Auto" (the default) follows the Windows display language;
    /// anything unsupported falls back to English.
    /// </summary>
    public static void Apply(string? preference)
    {
        var token = string.IsNullOrWhiteSpace(preference) ? "Auto" : preference.Trim();

        string language;
        if (token.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            language = ResolveLanguage(OsCulture);
        }
        else
        {
            language = Catalogs.ContainsKey(token) ? token.ToLowerInvariant() : "en";
        }

        Preference = token.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : language;
        Language = language;
        _culture = CultureInfo.GetCultureInfo(language);
        _active = Catalogs[language];

        CultureInfo.DefaultThreadCurrentCulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _culture;

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string ResolveLanguage(CultureInfo culture)
    {
        var code = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Catalogs.ContainsKey(code) ? code : "en";
    }
}
