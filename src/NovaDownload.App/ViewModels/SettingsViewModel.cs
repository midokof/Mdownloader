using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.App.Services;
using NovaDownload.BrowserIntegration;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

public partial class SettingsViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly BrowserIntegrationService _bridge;
    private readonly TorrentDownloadService? _torrent;
    private bool _loading;

    [ObservableProperty] private string downloadDirectory = string.Empty;
    [ObservableProperty] private int maxConcurrentDownloads = 3;
    [ObservableProperty] private bool notifyOnComplete = true;
    [ObservableProperty] private bool minimizeToTray;
    [ObservableProperty] private bool isDark;
    [ObservableProperty] private bool hasSaved;

    // BitTorrent
    [ObservableProperty] private int torrentListenPort = 51413;
    [ObservableProperty] private bool torrentEnableDht = true;
    [ObservableProperty] private bool torrentEnablePex = true;
    [ObservableProperty] private bool torrentEnableLpd = true;
    [ObservableProperty] private bool torrentEnablePortForwarding = true;
    [ObservableProperty] private bool torrentSeedAfterCompletion = true;

    [ObservableProperty] private bool torrentHandleMagnetLinks = true;
    [ObservableProperty] private int torrentMaxUploadKib;
    [ObservableProperty] private int torrentMaxDownloadKib;
    [ObservableProperty] private int torrentConnectionsPerTorrent = 60;
    [ObservableProperty] private int torrentMaxConnections = 200;

    // Browser integration
    [ObservableProperty] private bool enableBrowserIntegration = true;
    [ObservableProperty] private bool activateOnBrowserDownload = true;
    [ObservableProperty] private string browserMessage = string.Empty;

    private string _registeredBrowsers = string.Empty;
    private bool _nativeHostFound = true;

    // Language chips: exactly one is checked, and the checked one owns the preference.
    [ObservableProperty] private bool useSystemLanguage = true;
    [ObservableProperty] private bool useEnglish;
    [ObservableProperty] private bool useFrench;

    public SettingsViewModel(ISettingsService settings, ThemeService theme, BrowserIntegrationService bridge,
        TorrentDownloadService? torrent = null)
    {
        _settings = settings;
        _theme = theme;
        _bridge = bridge;
        _torrent = torrent;
        Load();
        RefreshBrowserState();
    }

    public string SavedMessage => HasSaved ? Localizer.Get("Set_Saved") : string.Empty;

    /// <summary>Registration + bridge state, recomposed for the active language.</summary>
    public string BrowserStatusText
    {
        get
        {
            if (!_nativeHostFound) return Localizer.Get("Set_BrowserHostMissing");
            var scope = _registeredBrowsers.Length == 0
                ? Localizer.Get("Set_BrowserNone")
                : Localizer.Format("Set_BrowserRegisteredFor", _registeredBrowsers);
            return Localizer.Format(_bridge.IsRunning ? "Set_BrowserStatusReady" : "Set_BrowserStatusIdle", scope);
        }
    }

    public string BrowserIdsText =>
        Localizer.Format("Set_BrowserIds", BrowserIntegrationInfo.ChromiumExtensionId, BrowserIntegrationInfo.FirefoxExtensionId);

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
            EnableBrowserIntegration = current.EnableBrowserIntegration;
            ActivateOnBrowserDownload = current.ActivateOnBrowserDownload;
            IsDark = _theme.IsDark;

            TorrentListenPort = current.TorrentListenPort;
            TorrentEnableDht = current.TorrentEnableDht;
            TorrentEnablePex = current.TorrentEnablePex;
            TorrentEnableLpd = current.TorrentEnableLsd;
            TorrentEnablePortForwarding = current.TorrentEnablePortForwarding;
            TorrentSeedAfterCompletion = current.TorrentSeedAfterCompletion;
            TorrentHandleMagnetLinks = current.TorrentHandleMagnetLinks;
            TorrentMaxUploadKib = current.TorrentMaxUploadKib;
            TorrentMaxDownloadKib = current.TorrentMaxDownloadKib;
            TorrentConnectionsPerTorrent = current.TorrentConnectionsPerTorrent;
            TorrentMaxConnections = current.TorrentMaxConnections;

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

    protected override void OnLanguageChanged() => Raise(
        nameof(SavedMessage), nameof(BrowserStatusText), nameof(BrowserIdsText));

    partial void OnHasSavedChanged(bool value) => Raise(nameof(SavedMessage));

    partial void OnEnableBrowserIntegrationChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.EnableBrowserIntegration = value;
        _ = _settings.SaveAsync();
        Raise(nameof(BrowserStatusText));
    }

    partial void OnActivateOnBrowserDownloadChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.ActivateOnBrowserDownload = value;
        _ = _settings.SaveAsync();
    }

    /// <summary>
    /// Takes the <c>magnet:</c> scheme, or gives it back, the moment the toggle moves: the setting
    /// is not a hint for the next start, it is what the registry says right now.
    /// </summary>
    partial void OnTorrentHandleMagnetLinksChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.TorrentHandleMagnetLinks = value;
        _ = _settings.SaveAsync();
        NovaDownload.App.App.ApplyMagnetHandler(_settings.Current);
    }

    /// <summary>Reads the current per-user registration and bridge state without changing anything.</summary>
    private void RefreshBrowserState()
    {
        try
        {
            _nativeHostFound = NativeHostLocator.TryResolve(out _) != null;
            var report = _bridge.InspectRegistration();
            _registeredBrowsers = string.Join(", ", report.Registered
                .Select(entry => string.IsNullOrEmpty(entry.BrowserVersion)
                    ? entry.DisplayName
                    : $"{entry.DisplayName} {entry.BrowserVersion}"));
        }
        catch
        {
            _nativeHostFound = false;
            _registeredBrowsers = string.Empty;
        }

        Raise(nameof(BrowserStatusText));
    }

    /// <summary>Writes the native messaging manifests and registry keys for the current user.</summary>
    [RelayCommand]
    private void RegisterBrowserIntegration()
    {
        var report = _bridge.RegisterForCurrentUser(out var error);
        var names = string.Join(", ", report.Registered.Select(entry => entry.DisplayName));

        BrowserMessage = error is not null
            ? Localizer.Format("Set_BrowserRegistrationFailed", error)
            : names.Length == 0
                ? Localizer.Get("Set_BrowserHostMissing")
                : Localizer.Format("Set_BrowserRegistered", names);

        RefreshBrowserState();
    }

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

        current.TorrentListenPort = Math.Clamp(TorrentListenPort, 0, 65535);
        current.TorrentEnableDht = TorrentEnableDht;
        current.TorrentEnablePex = TorrentEnablePex;
        current.TorrentEnableLsd = TorrentEnableLpd;
        current.TorrentEnablePortForwarding = TorrentEnablePortForwarding;
        current.TorrentSeedAfterCompletion = TorrentSeedAfterCompletion;
        current.TorrentHandleMagnetLinks = TorrentHandleMagnetLinks;
        current.TorrentMaxUploadKib = Math.Clamp(TorrentMaxUploadKib, 0, 1_000_000);
        current.TorrentMaxDownloadKib = Math.Clamp(TorrentMaxDownloadKib, 0, 1_000_000);
        current.TorrentConnectionsPerTorrent = Math.Clamp(TorrentConnectionsPerTorrent, 5, 500);
        current.TorrentMaxConnections = Math.Clamp(TorrentMaxConnections, 10, 2000);

        HasSaved = false;
        await _settings.SaveAsync();

        // The session is already running: push the limits that can change live.
        if (_torrent != null)
        {
            await _torrent.ApplyOptionsAsync(new TorrentEngineOptions
            {
                ListenPort = current.TorrentListenPort,
                EnableDht = current.TorrentEnableDht,
                EnablePeerExchange = current.TorrentEnablePex,
                EnableLocalPeerDiscovery = current.TorrentEnableLsd,
                EnablePortForwarding = current.TorrentEnablePortForwarding,
                SeedAfterCompletion = current.TorrentSeedAfterCompletion,
                MaxUploadKib = current.TorrentMaxUploadKib,
                MaxDownloadKib = current.TorrentMaxDownloadKib,
                ConnectionsPerTorrent = current.TorrentConnectionsPerTorrent,
                MaxConnections = current.TorrentMaxConnections
            });
        }

        HasSaved = true;
    }

    public void RefreshTheme(string theme) => IsDark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
}
