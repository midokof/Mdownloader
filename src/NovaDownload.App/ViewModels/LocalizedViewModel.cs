using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// Base for view models that render composed catalog text. Subscribes once to the language
/// change and lets each view model re-raise exactly the properties it composes.
/// </summary>
public abstract class LocalizedViewModel : ObservableObject, IDisposable
{
    private readonly EventHandler _languageHandler;

    protected LocalizedViewModel()
    {
        _languageHandler = (_, _) => OnLanguageChanged();
        Localizer.LanguageChanged += _languageHandler;
    }

    protected virtual void OnLanguageChanged()
    {
    }

    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }

    /// <summary>A dialog view model lives as long as its window; the static event must not hold it.</summary>
    public void Dispose() => Localizer.LanguageChanged -= _languageHandler;
}
