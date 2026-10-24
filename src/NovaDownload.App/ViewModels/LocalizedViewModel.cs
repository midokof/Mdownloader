using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// Base for view models that render composed catalog text. Subscribes once to the language
/// change and lets each view model re-raise exactly the properties it composes.
/// </summary>
public abstract class LocalizedViewModel : ObservableObject
{
    protected LocalizedViewModel() => Localizer.LanguageChanged += (_, _) => OnLanguageChanged();

    protected virtual void OnLanguageChanged()
    {
    }

    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }
}
