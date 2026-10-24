using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.App.Services;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

public partial class SettingsViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private bool _loading;

    [ObservableProperty] private string downloadDirectory = string.Empty;
    [ObservableProperty] private int maxConcurrentDownloads = 3;
    [ObservableProperty] private bool notifyOnComplete = true;
    [ObservableProperty] private bool minimizeToTray;
    [ObservableProperty] private bool isDark;
    [ObservableProperty] private bool hasSaved;

    // Language chips: exactly one is checked, and the checked one owns the preference.
    [ObservableProperty] private bool useSystemLanguage = true;
    [ObservableProperty] private bool useEnglish;
    [ObservableProperty] private bool useFrench;

    public SettingsViewModel(ISettingsService settings, ThemeService theme)
    {
        _settings = settings;
        _theme = theme;
        Load();
    }

    public string SavedMessage => HasSaved ? Localizer.Get("Set_Saved") : string.Empty;

    private void Load()
    {
        _loading = true;
        try
        {
            var current = _settings.Current;
            DownloadDirectory = current.DefaultDownloadDirectory;
            MaxConcurrentDownloads = current.MaxConcurrentDownloads;
            NotifyOnComplete = current.NotifyOnComplete;
            MinimizeToTray = current.MinimizeToTray;
            IsDark = _theme.IsDark;

            switch (current.Language)
            {
                case "en":
                    UseEnglish = true;
                    break;
                case "fr":
                    UseFrench = true;
                    break;
                default:
                    UseSystemLanguage = true;
                    break;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void OnLanguageChanged() => Raise(nameof(SavedMessage));

    partial void OnHasSavedChanged(bool value) => Raise(nameof(SavedMessage));

    partial void OnUseSystemLanguageChanged(bool value)
    {
        if (value) ApplyLanguage("Auto");
    }

    partial void OnUseEnglishChanged(bool value)
    {
        if (value) ApplyLanguage("en");
    }

    partial void OnUseFrenchChanged(bool value)
    {
        if (value) ApplyLanguage("fr");
    }

    /// <summary>
    /// Applies and persists a language immediately — like the theme, so the switch is
    /// visible while the user is still on the page.
    /// </summary>
    private void ApplyLanguage(string preference)
    {
        if (_loading) return;

        Localizer.Apply(preference);
        _settings.Current.Language = Localizer.Preference;
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DownloadDirectory };
        if (dialog.ShowDialog() == true) DownloadDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void SetLight()
    {
        IsDark = false;
        ApplyTheme();
    }

    [RelayCommand]
    private void SetDark()
    {
        IsDark = true;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var target = IsDark ? "Dark" : "Light";
        _theme.Apply(target);
        _settings.Current.Theme = target;
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private void OpenDownloadsFolder() => ShellHelper.OpenFolder(DownloadDirectory);

    [RelayCommand]
    private async Task Save()
    {
        var current = _settings.Current;
        if (!string.IsNullOrWhiteSpace(DownloadDirectory)) current.DefaultDownloadDirectory = DownloadDirectory.Trim();
        current.MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        current.NotifyOnComplete = NotifyOnComplete;
        current.MinimizeToTray = MinimizeToTray;

        HasSaved = false;
        await _settings.SaveAsync();
        HasSaved = true;
    }

    public void RefreshTheme(string theme) => IsDark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
}
