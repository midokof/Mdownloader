using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// A one-field prompt, used by the "+" of the category combo. It validates that the name is not
/// empty and that it is not already in the list.
/// </summary>
public partial class PromptViewModel : LocalizedViewModel
{
    private readonly Func<string, bool>? _exists;

    public PromptViewModel(string titleKey, string labelKey, string? initial = null, Func<string, bool>? exists = null)
    {
        _titleKey = titleKey;
        _labelKey = labelKey;
        _exists = exists;
        value = initial ?? string.Empty;
    }

    private readonly string _titleKey;
    private readonly string _labelKey;

    [ObservableProperty] private string value = string.Empty;
    [ObservableProperty] private string? error;

    public string Title => Localizer.Get(_titleKey);

    public string Label => Localizer.Get(_labelKey);

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnValueChanged(string value) => Error = null;

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title), nameof(Label));
    }

    /// <summary>True when the field holds a usable, new name.</summary>
    public bool Validate()
    {
        var trimmed = Value.Trim();

        if (trimmed.Length == 0)
        {
            Error = Localizer.Get("Prompt_Empty");
            return false;
        }

        if (_exists != null && _exists(trimmed))
        {
            Error = Localizer.Get("Prompt_Exists");
            return false;
        }

        Value = trimmed;
        Error = null;
        return true;
    }
}
