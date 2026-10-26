using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.App.Services;
using NovaDownload.BrowserIntegration;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Http;
using NovaDownload.DownloadEngine.Services;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// One editable row of the "Catégories" tab: where files of that type land, and the extensions the
/// classifier files under it.
/// </summary>
public partial class CategoryFolderViewModel : ObservableObject
{
    public CategoryFolderViewModel(DownloadCategory category, string folder)
    {
        Category = category;
        Folder = folder;
        Extensions = string.Join(" · ", DownloadClassifier.Extensions(category).Select(extension => extension.ToUpperInvariant()));
    }

    public DownloadCategory Category { get; }

    public string Name => Category.ToString();

    public string Label => DownloadClassifier.Text(Category);

    public string Extensions { get; }

    [ObservableProperty] private string folder;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The Options dialog: every tab writes a real setting, and OK pushes the ones the running engines
/// can take live (rate limit, retries, segment count, proxy, torrent session).
/// </summary>
public partial class OptionsViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly BrowserIntegrationService _bridge;
    private readonly HttpDownloadService? _http;
    private readonly TorrentDownloadService? _torrent;
    private bool _loading;

    /// <summary>Raised after OK: the shell rebuilds its pane and re-reads the preferences.</summary>
    public event EventHandler? Applied;

    [ObservableProperty] private string selectedTab = "General";
    [ObservableProperty] private string downloadDirectory = string.Empty;
    [ObservableProperty] private int maxConcurrentDownloads = 3;
    [ObservableProperty] private int connectionsPerDownload = 4;
    [ObservableProperty] private int retryCount = 5;
    [ObservableProperty] private int retryDelayMs = 400;
    [ObservableProperty] private int speedLimitKib;
    [ObservableProperty] private string proxyMode = "System";
    [ObservableProperty] private string proxyHost = string.Empty;
    [ObservableProperty] private int proxyPort;
    [ObservableProperty] private bool notifyOnComplete = true;
    [ObservableProperty] private bool minimizeToTray;
    [ObservableProperty] private bool isDark = true;
    [ObservableProperty] private bool useSystemLanguage;
    [ObservableProperty] private bool useEnglish;
    [ObservableProperty] private bool useFrench;
    [ObservableProperty] private CategoryFolderViewModel? selectedCategoryRow;
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
    [ObservableProperty] private int torrentConnectionsPerTorrent = 60;
    [ObservableProperty] private int torrentMaxConnections = 200;

    // Browser integration
    [ObservableProperty] private bool enableBrowserIntegration = true;
    [ObservableProperty] private bool activateOnBrowserDownload = true;
    [ObservableProperty] private string browserMessage = string.Empty;

    private string _registeredBrowsers = string.Empty;
    private bool _nativeHostFound = true;

    public ObservableCollection<CategoryFolderViewModel> CategoryRows { get; } = new();

    public OptionsViewModel(ISettingsService settings, ThemeService theme, BrowserIntegrationService bridge,
        HttpDownloadService? http = null, TorrentDownloadService? torrent = null)
    {
        _settings = settings;
        _theme = theme;
        _bridge = bridge;
        _http = http;
        _torrent = torrent;

        Load();
        RefreshBrowserState();
    }

    public bool IsGeneralTab => SelectedTab == "General";
    public bool IsConnectionsTab => SelectedTab == "Connections";
    public bool IsProxyTab => SelectedTab == "Proxy";
    public bool IsSpeedTab => SelectedTab == "Speed";
    public bool IsCategoriesTab => SelectedTab == "Categories";
    public bool IsTorrentsTab => SelectedTab == "Torrents";
    public bool IsInterfaceTab => SelectedTab == "Interface";
    public bool IsBrowserTab => SelectedTab == "Browser";

    public string SavedMessage => HasSaved ? Localizer.Get("Set_Saved") : string.Empty;

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

    /// <summary>Folder shown under the category list: the one the selected row points at.</summary>
    public string SelectedCategoryFolder => SelectedCategoryRow?.Folder ?? Localizer.Get("Common_Dash");

    partial void OnSelectedTabChanged(string value)
    {
        Raise(nameof(IsGeneralTab), nameof(IsConnectionsTab), nameof(IsProxyTab), nameof(IsSpeedTab),
            nameof(IsCategoriesTab), nameof(IsTorrentsTab), nameof(IsInterfaceTab), nameof(IsBrowserTab));
    }

    partial void OnSelectedCategoryRowChanged(CategoryFolderViewModel? value) =>
        OnPropertyChanged(nameof(SelectedCategoryFolder));

    partial void OnHasSavedChanged(bool value) => Raise(nameof(SavedMessage));

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (!string.IsNullOrEmpty(tab)) SelectedTab = tab;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var current = _settings.Current;

            DownloadDirectory = current.DefaultDownloadDirectory;
            MaxConcurrentDownloads = current.MaxConcurrentDownloads;
            ConnectionsPerDownload = current.ConnectionsPerDownload;
            RetryCount = current.RetryCount;
            RetryDelayMs = current.RetryDelayMs;
            SpeedLimitKib = current.SpeedLimitKib;
            ProxyMode = NormalizeProxy(current.ProxyMode);
            ProxyHost = current.ProxyHost;
            ProxyPort = current.ProxyPort;
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

            CategoryRows.Clear();
            foreach (var category in new[]
                     {
                         DownloadCategory.Compressed, DownloadCategory.Documents, DownloadCategory.Music,
                         DownloadCategory.Programs, DownloadCategory.Video, DownloadCategory.Torrents
                     })
            {
                var folder = current.CategoryFolders.TryGetValue(category.ToString(), out var configured)
                    ? configured
                    : string.Empty;
                CategoryRows.Add(new CategoryFolderViewModel(category, folder));
            }

            SelectedCategoryRow = CategoryRows.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    private static string NormalizeProxy(string? mode) => mode switch
    {
        "Direct" => "Direct",
        "Manual" => "Manual",
        _ => "System"
    };

    protected override void OnLanguageChanged()
    {
        Raise(nameof(SavedMessage), nameof(BrowserStatusText), nameof(BrowserIdsText), nameof(SelectedCategoryFolder));
        foreach (var row in CategoryRows) row.RefreshLabel();
    }

    // ---- folders ---------------------------------------------------------------

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DownloadDirectory };
        if (dialog.ShowDialog() == true) DownloadDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseCategory()
    {
        var row = SelectedCategoryRow;
        if (row == null) return;

        var start = string.IsNullOrWhiteSpace(row.Folder) ? DownloadDirectory : row.Folder;
        var dialog = new OpenFolderDialog { InitialDirectory = start };
        if (dialog.ShowDialog() != true) return;

        row.Folder = dialog.FolderName;
        OnPropertyChanged(nameof(SelectedCategoryFolder));
    }

    /// <summary>Clears the row so the category falls back to the default folder.</summary>
    [RelayCommand]
    private void DefaultCategory()
    {
        if (SelectedCategoryRow == null) return;
        SelectedCategoryRow.Folder = string.Empty;
        OnPropertyChanged(nameof(SelectedCategoryFolder));
    }

    // ---- theme and language ----------------------------------------------------

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

    private void ApplyLanguage(string preference)
    {
        if (_loading) return;

        Localizer.Apply(preference);
        _settings.Current.Language = Localizer.Preference;
        _ = _settings.SaveAsync();
    }

    // ---- browser ---------------------------------------------------------------

    /// <summary>
    /// Takes the <c>magnet:</c> scheme, or gives it back, the moment the toggle moves: the setting
    /// is not a hint for the next start, it is what the registry says right now.
    /// </summary>
    partial void OnTorrentHandleMagnetLinksChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.TorrentHandleMagnetLinks = value;
        _ = _settings.SaveAsync();
        App.ApplyMagnetHandler(_settings.Current);
    }

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
        catch (Exception)
        {
            _nativeHostFound = false;
            _registeredBrowsers = string.Empty;
        }

        Raise(nameof(BrowserStatusText));
    }

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

    // ---- apply -----------------------------------------------------------------

    [RelayCommand]
    private async Task Save()
    {
        var current = _settings.Current;

        if (!string.IsNullOrWhiteSpace(DownloadDirectory))
            current.DefaultDownloadDirectory = DownloadDirectory.Trim();

        current.MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        current.ConnectionsPerDownload = Math.Clamp(ConnectionsPerDownload, 1, 16);
        current.RetryCount = Math.Clamp(RetryCount, 0, 20);
        current.RetryDelayMs = Math.Clamp(RetryDelayMs, 50, 10000);
        current.SpeedLimitKib = Math.Clamp(SpeedLimitKib, 0, 1_000_000);

        current.ProxyMode = NormalizeProxy(ProxyMode);
        current.ProxyHost = ProxyHost.Trim();
        current.ProxyPort = Math.Clamp(ProxyPort, 0, 65535);

        current.NotifyOnComplete = NotifyOnComplete;
        current.MinimizeToTray = MinimizeToTray;

        current.TorrentListenPort = Math.Clamp(TorrentListenPort, 0, 65535);
        current.TorrentEnableDht = TorrentEnableDht;
        current.TorrentEnablePex = TorrentEnablePex;
        current.TorrentEnableLsd = TorrentEnableLpd;
        current.TorrentEnablePortForwarding = TorrentEnablePortForwarding;
        current.TorrentSeedAfterCompletion = TorrentSeedAfterCompletion;
        current.TorrentMaxUploadKib = Math.Clamp(TorrentMaxUploadKib, 0, 1_000_000);

        // One download ceiling for both engines: a second field would just fight this one.
        current.TorrentMaxDownloadKib = current.SpeedLimitKib;
        current.TorrentConnectionsPerTorrent = Math.Clamp(TorrentConnectionsPerTorrent, 5, 500);
        current.TorrentMaxConnections = Math.Clamp(TorrentMaxConnections, 10, 2000);

        current.CategoryFolders = CategoryRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Folder))
            .ToDictionary(row => row.Name, row => row.Folder.Trim(), StringComparer.OrdinalIgnoreCase);

        HasSaved = false;
        await _settings.SaveAsync();

        ApplyLive(current);
        Applied?.Invoke(this, EventArgs.Empty);

        HasSaved = true;
    }

    /// <summary>Pushes what a running engine can take without a restart.</summary>
    private void ApplyLive(Core.Models.AppSettings current)
    {
        if (_http != null)
        {
            _http.Options.Segments = current.ConnectionsPerDownload;
            _http.Options.MaxRetries = current.RetryCount;
            _http.Options.RetryBaseDelayMs = current.RetryDelayMs;
            _http.GlobalSpeedLimitBps = (long)current.SpeedLimitKib * 1024;

            try
            {
                switch (current.ProxyMode)
                {
                    case "Direct":
                        _http.ApplyProxy(HttpProxyMode.Direct);
                        break;
                    case "Manual" when current.ProxyPort is > 0 and <= 65535 &&
                                        !string.IsNullOrWhiteSpace(current.ProxyHost):
                        _http.ApplyProxy(HttpProxyMode.Manual, current.ProxyHost, current.ProxyPort);
                        break;
                    default:
                        _http.ApplyProxy(HttpProxyMode.System);
                        break;
                }
            }
            catch (ArgumentException)
            {
                // A half-filled manual proxy is ignored rather than pushed: the mode stays System.
                _http.ApplyProxy(HttpProxyMode.System);
            }
        }

        if (_torrent == null) return;

        _ = _torrent.ApplyOptionsAsync(new TorrentEngineOptions
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

    public void RefreshTheme(string theme) => IsDark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
}
