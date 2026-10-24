using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaDownload.App.Services;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

public partial class MainWindowViewModel : LocalizedViewModel
{
    private readonly DashboardViewModel _dashboard;
    private readonly DownloadsViewModel _downloads;
    private readonly AddDownloadViewModel _addDownload;
    private readonly HistoryViewModel _history;
    private readonly SettingsViewModel _settings;
    private readonly ThemeService _theme;
    private readonly ISettingsService _settingsService;

    private string _titleKey = "Page_Dashboard_Title";
    private string _subtitleKey = "Page_Dashboard_Subtitle";

    [ObservableProperty]
    private object? currentView;

    [ObservableProperty]
    private AppPage page = AppPage.Dashboard;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isDarkTheme;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        DownloadsViewModel downloads,
        AddDownloadViewModel addDownload,
        HistoryViewModel history,
        SettingsViewModel settings,
        ThemeService theme,
        ISettingsService settingsService)
    {
        _dashboard = dashboard;
        _downloads = downloads;
        _addDownload = addDownload;
        _history = history;
        _settings = settings;
        _theme = theme;
        _settingsService = settingsService;

        isDarkTheme = theme.IsDark;
        CurrentView = dashboard;

        _addDownload.DownloadStarted += (_, _) => ShowDownloads();
        _dashboard.NewDownloadRequested += (_, _) => ShowAddDownload();

        _dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.ActiveDownloads) or nameof(DashboardViewModel.PausedDownloads))
                OnPropertyChanged(nameof(SidebarSummary));
        };
    }

    public bool IsDashboard => Page == AppPage.Dashboard;
    public bool IsDownloads => Page == AppPage.Downloads;
    public bool IsAddDownload => Page == AppPage.AddDownload;
    public bool IsHistory => Page == AppPage.History;
    public bool IsSettings => Page == AppPage.Settings;

    public DashboardViewModel Dashboard => _dashboard;

    public string PageTitle => Localizer.Get(_titleKey);

    public string PageSubtitle => Localizer.Get(_subtitleKey);

    /// <summary>"3 active · 1 paused" — live counters, live language.</summary>
    public string SidebarSummary =>
        Localizer.Format("Sidebar_Summary", _dashboard.ActiveDownloads, _dashboard.PausedDownloads);

    public string ThemeLabel => IsDarkTheme ? Localizer.Get("Theme_Light") : Localizer.Get("Theme_Dark");

    protected override void OnLanguageChanged() => Raise(
        nameof(PageTitle), nameof(PageSubtitle), nameof(SidebarSummary), nameof(ThemeLabel));

    partial void OnIsDarkThemeChanged(bool value) => OnPropertyChanged(nameof(ThemeLabel));

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsDownloads));
        OnPropertyChanged(nameof(IsAddDownload));
        OnPropertyChanged(nameof(IsHistory));
        OnPropertyChanged(nameof(IsSettings));
    }

    partial void OnSearchTextChanged(string value)
    {
        _downloads.SearchText = value;
        if (!string.IsNullOrWhiteSpace(value) && Page != AppPage.Downloads) ShowDownloads();
    }

    [RelayCommand]
    private void ShowDashboard() =>
        Navigate(AppPage.Dashboard, _dashboard, "Page_Dashboard_Title", "Page_Dashboard_Subtitle");

    [RelayCommand]
    private void ShowDownloads() =>
        Navigate(AppPage.Downloads, _downloads, "Page_Downloads_Title", "Page_Downloads_Subtitle");

    [RelayCommand]
    private void ShowAddDownload() =>
        Navigate(AppPage.AddDownload, _addDownload, "Page_Add_Title", "Page_Add_Subtitle");

    [RelayCommand]
    private void ShowHistory() =>
        Navigate(AppPage.History, _history, "Page_History_Title", "Page_History_Subtitle");

    [RelayCommand]
    private void ShowSettings() =>
        Navigate(AppPage.Settings, _settings, "Page_Settings_Title", "Page_Settings_Subtitle");

    [RelayCommand]
    private async Task ToggleTheme()
    {
        var target = _theme.Toggle();
        _theme.Apply(target);
        _settingsService.Current.Theme = target;
        await _settingsService.SaveAsync();
        IsDarkTheme = _theme.IsDark;
        _settings.RefreshTheme(target);
    }

    private void Navigate(AppPage page, object view, string titleKey, string subtitleKey)
    {
        Page = page;
        CurrentView = view;
        _titleKey = titleKey;
        _subtitleKey = subtitleKey;
        Raise(nameof(PageTitle), nameof(PageSubtitle));
    }
}
