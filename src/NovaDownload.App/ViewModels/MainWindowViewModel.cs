using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using NovaDownload.App.Services;
using NovaDownload.App.Views;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// The shell: menu bar, toolbar, category pane, status bar, tray icon and the dialogs they open.
/// Every command here is reachable from at least one of those surfaces — there is no dead entry.
/// </summary>
public partial class MainWindowViewModel : LocalizedViewModel
{
    private readonly IDownloadService _service;
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DownloadStore _store;
    private readonly IServiceProvider _services;
    private readonly HttpDownloadService? _http;
    private readonly TorrentDownloadService? _torrent;
    private readonly QueueScheduler _scheduler;

    private readonly HashSet<Guid> _completedAnnounced = new();
    private string? _statusKey;

    /// <summary>The window is hidden: the tray icon is the only way back.</summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>Ctrl+F: the view puts the caret in the search box.</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Ctrl+A: the list selects every visible row.</summary>
    public event EventHandler? SelectAllRequested;

    public DownloadsViewModel Downloads { get; }

    public ObservableCollection<CategoryNodeViewModel> Categories { get; } = new();

    public TrayService Tray { get; }

    [ObservableProperty] private CategoryNodeViewModel? selectedCategoryNode;

    [ObservableProperty] private bool showCategoryPane = true;
    [ObservableProperty] private bool showToolbar = true;
    [ObservableProperty] private bool showStatusBar = true;
    [ObservableProperty] private bool isDark;
    [ObservableProperty] private bool isFrench;
    [ObservableProperty] private bool isEnglish;
    [ObservableProperty] private string statusMessage = string.Empty;

    public MainWindowViewModel(IDownloadService service, ISettingsService settings, ThemeService theme,
        DownloadStore store, DownloadsViewModel downloads, IServiceProvider services,
        HttpDownloadService? http = null, TorrentDownloadService? torrent = null)
    {
        _service = service;
        _settings = settings;
        _theme = theme;
        _store = store;
        _services = services;
        _http = http;
        _torrent = torrent;

        Downloads = downloads;

        ShowCategoryPane = settings.Current.ShowCategoryPane;
        IsDark = _theme.IsDark;
        RefreshLanguageFlags();

        BuildCategories();

        _store.Refreshed += (_, _) => OnStoreRefreshed();

        Tray = new TrayService();
        Tray.OpenRequested += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        Tray.ExitRequested += (_, _) => ExitCommand.Execute(null);
        Tray.OptionsRequested += (_, _) => OptionsCommand.Execute(null);
        Tray.PauseAllRequested += (_, _) => Downloads.PauseAllCommand.Execute(null);
        Tray.ResumeAllRequested += (_, _) => Downloads.ResumeAllCommand.Execute(null);

        _scheduler = new QueueScheduler(_settings, service.ResumeAllAsync, service.PauseAllAsync);
        _scheduler.Reported += (_, message) => ReportText(message);
    }

    public string Title => "NovaDownload";

    public string VersionText => Localizer.Format("About_Version", AppVersion);

    private static string AppVersion =>
        typeof(MainWindowViewModel).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    /// <summary>
    /// Messages that exist as catalog keys are reported by key, so switching the language also
    /// switches the line already sitting in the status bar.
    /// </summary>
    private void Report(string key)
    {
        _statusKey = key;
        StatusMessage = Localizer.Get(key);
    }

    /// <summary>An already-composed line (speed, schedule): it does not follow a language switch.</summary>
    private void ReportText(string message)
    {
        _statusKey = null;
        StatusMessage = message;
    }

    partial void OnShowCategoryPaneChanged(bool value)
    {
        _settings.Current.ShowCategoryPane = value;
        _ = _settings.SaveAsync();
    }

    // ---- start-up wiring -------------------------------------------------------

    /// <summary>
    /// Called once the window exists: the scheduler needs a dispatcher and the tray needs a window
    /// to come back to.
    /// </summary>
    public void StartBackgroundServices()
    {
        ApplyEngineOptions(_settings.Current);
        _scheduler.Start();

        // Rows restored from the archive are already complete; they must not be announced again.
        foreach (var item in _store.Items.Where(row => row.Status == DownloadStatus.Completed))
            _completedAnnounced.Add(item.Id);
    }

    /// <summary>Pushes the persisted preferences into the running engines (rate, retries, proxy).</summary>
    public void ApplyEngineOptions(AppSettings current)
    {
        if (_http != null)
        {
            _http.Options.Segments = Math.Clamp(current.ConnectionsPerDownload, 1, 16);
            _http.Options.MaxRetries = Math.Clamp(current.RetryCount, 0, 20);
            _http.Options.RetryBaseDelayMs = Math.Clamp(current.RetryDelayMs, 50, 10000);
            _http.GlobalSpeedLimitBps = (long)Math.Clamp(current.SpeedLimitKib, 0, 1_000_000) * 1024;

            try
            {
                switch (current.ProxyMode)
                {
                    case "Direct":
                        _http.ApplyProxy(DownloadEngine.Http.HttpProxyMode.Direct);
                        break;
                    case "Manual" when current.ProxyPort is > 0 and <= 65535 &&
                                        !string.IsNullOrWhiteSpace(current.ProxyHost):
                        _http.ApplyProxy(DownloadEngine.Http.HttpProxyMode.Manual, current.ProxyHost,
                            current.ProxyPort);
                        break;
                    default:
                        _http.ApplyProxy(DownloadEngine.Http.HttpProxyMode.System);
                        break;
                }
            }
            catch (ArgumentException)
            {
                _http.ApplyProxy(DownloadEngine.Http.HttpProxyMode.System);
            }
        }

        if (_torrent != null)
        {
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
    }

    // ---- category pane ---------------------------------------------------------

    /// <summary>
    /// "Tous les téléchargements" carries the six built-in categories plus the ones the user
    /// created with "+", then the three status scopes sit as siblings.
    /// </summary>
    private void BuildCategories()
    {
        var selectedTag = SelectedCategoryNode?.Tag ?? "All";

        Categories.Clear();

        var all = new CategoryNodeViewModel("All", "IconDownload", "Cat_All");
        all.Add(new CategoryNodeViewModel("Compressed", "IconArchive", "Cat_Compressed"));
        all.Add(new CategoryNodeViewModel("Documents", "IconFileList", "Cat_Documents"));
        all.Add(new CategoryNodeViewModel("Music", "IconMusic", "Cat_Music"));
        all.Add(new CategoryNodeViewModel("Programs", "IconProgram", "Cat_Programs"));
        all.Add(new CategoryNodeViewModel("Video", "IconVideo", "Cat_Video"));
        all.Add(new CategoryNodeViewModel("Torrents", "IconTorrent", "Cat_Torrents"));

        foreach (var custom in _settings.Current.CustomCategories)
        {
            if (string.IsNullOrWhiteSpace(custom)) continue;
            if (all.Children.Any(node => string.Equals(node.Label, custom, StringComparison.OrdinalIgnoreCase))) continue;
            all.Add(new CategoryNodeViewModel($"custom:{custom}", "IconFolder", literal: custom));
        }

        Categories.Add(all);
        Categories.Add(new CategoryNodeViewModel("Unfinished", "IconClock", "Cat_Unfinished"));
        Categories.Add(new CategoryNodeViewModel("Finished", "IconCheck", "Cat_Finished"));
        Categories.Add(new CategoryNodeViewModel("Queue", "IconQueue", "Cat_Queue"));

        SelectedCategoryNode = FindNode(selectedTag) ?? Categories[0];
    }

    private CategoryNodeViewModel? FindNode(string tag)
    {
        foreach (var node in Categories)
        {
            if (node.Tag == tag) return node;
            var child = node.Children.FirstOrDefault(candidate => candidate.Tag == tag);
            if (child != null) return child;
        }

        return null;
    }

    /// <summary>The tree view reports a new selection; the list follows its tag.</summary>
    public void SelectNode(CategoryNodeViewModel? node)
    {
        if (node == null) return;
        SelectedCategoryNode = node;
        Downloads.SelectScope(node.Tag);
    }

    [RelayCommand]
    private void ToggleCategoryPane() => ShowCategoryPane = !ShowCategoryPane;

    [RelayCommand]
    private void ToggleToolbar() => ShowToolbar = !ShowToolbar;

    [RelayCommand]
    private void ToggleStatusBar() => ShowStatusBar = !ShowStatusBar;

    // ---- add flow --------------------------------------------------------------

    [RelayCommand]
    private Task AddUrl() => AddUrlAsync(null);

    /// <summary>Ctrl+V and "Coller une URL": the clipboard is the first field of the add dialog.</summary>
    [RelayCommand]
    private Task PasteUrl()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText().Trim();
        }
        catch (Exception)
        {
            // Clipboard busy: the dialog opens empty rather than failing the shortcut.
        }

        return AddUrlAsync(text);
    }

    /// <summary>A drop or a shell hand-off: same dialog, prefilled, so the folder stays a choice.</summary>
    public async Task AcceptDropAsync(IEnumerable<string> sources)
    {
        var lines = sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .ToArray();

        if (lines.Length == 0) return;
        await AddUrlAsync(string.Join(Environment.NewLine, lines));
    }

    private async Task AddUrlAsync(string? prefill)
    {
        var add = new AddUrlViewModel(_settings);
        add.Reset(prefill);

        var window = new AddUrlWindow(add) { Owner = DialogOwner };
        var accepted = window.ShowDialog() == true;
        var folder = add.Folder;
        var sources = add.Accepted.ToArray();
        add.Dispose();

        if (!accepted || sources.Length == 0) return;
        await ConfirmSourcesAsync(sources, folder);
    }

    /// <summary>One confirmation per source, in order: the batch keeps the folder the user chose.</summary>
    private async Task ConfirmSourcesAsync(IReadOnlyList<string> sources, string folder)
    {
        foreach (var source in sources)
        {
            if (!await ConfirmOneAsync(source, folder)) continue;
            Downloads.Refresh();
        }

        // A "+" in the confirmation adds a pane node; the tree is rebuilt from the settings.
        BuildCategories();
    }

    private async Task<bool> ConfirmOneAsync(string source, string folder)
    {
        var confirmation = _services.GetRequiredService<ConfirmationViewModel>();
        await confirmation.InitializeAsync(source, folder);

        var window = new ConfirmationWindow(confirmation) { Owner = DialogOwner };
        var started = window.ShowDialog() == true;
        confirmation.Dispose();

        if (!started) return false;

        if (confirmation.StartedId is { } id)
        {
            var row = _store.Items.FirstOrDefault(item => item.Id == id);
            if (row != null) Downloads.SelectSingle(row);
        }

        return true;
    }

    [RelayCommand]
    private async Task OpenTorrent()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.Get("Dlg_OpenTorrent"),
            Filter = Localizer.Get("Dlg_TorrentFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        await AddUrlAsync(dialog.FileName);
    }

    // ---- dialogs ---------------------------------------------------------------

    [RelayCommand]
    private void Options()
    {
        var viewModel = _services.GetRequiredService<OptionsViewModel>();
        var window = new OptionsWindow(viewModel) { Owner = DialogOwner };

        var saved = window.ShowDialog() == true;
        viewModel.Dispose();

        if (!saved) return;

        IsDark = _theme.IsDark;
        RefreshLanguageFlags();
        BuildCategories();
        Downloads.Refresh();
        Report("Opt_Applied");
    }

    [RelayCommand]
    private void SpeedLimit()
    {
        var viewModel = new SpeedLimitViewModel(_settings, _http, _torrent);
        var window = new SpeedLimitWindow(viewModel) { Owner = DialogOwner };

        var saved = window.ShowDialog() == true;
        viewModel.Dispose();

        if (!saved) return;

        viewModel.Apply();
        ReportText(viewModel.Summary);
    }

    [RelayCommand]
    private void Scheduler()
    {
        var viewModel = new SchedulerViewModel(_settings);
        var window = new SchedulerWindow(viewModel) { Owner = DialogOwner };

        var saved = window.ShowDialog() == true;
        viewModel.Dispose();

        // Re-reads the window right away instead of waiting for the next tick.
        if (saved) _scheduler.Start();
    }

    [RelayCommand]
    private void About()
    {
        var window = new AboutWindow { Owner = DialogOwner };
        window.ShowDialog();
    }

    [RelayCommand]
    private void Help()
    {
        var window = new HelpWindow { Owner = DialogOwner };
        window.ShowDialog();
    }

    private Window? DialogOwner =>
        Application.Current?.MainWindow is { IsVisible: true } owner ? owner : null;

    // ---- view ----------------------------------------------------------------

    [RelayCommand]
    private void ToggleTheme()
    {
        var target = _theme.Toggle();
        _settings.Current.Theme = target;
        _ = _settings.SaveAsync();
        IsDark = _theme.IsDark;
    }

    [RelayCommand]
    private void SetFrench() => ApplyLanguage("fr");

    [RelayCommand]
    private void SetEnglish() => ApplyLanguage("en");

    private void ApplyLanguage(string preference)
    {
        Localizer.Apply(preference);
        _settings.Current.Language = Localizer.Preference;
        _ = _settings.SaveAsync();

        RefreshLanguageFlags();
        BuildCategories();
        Downloads.Refresh();
    }

    private void RefreshLanguageFlags()
    {
        IsFrench = Localizer.Language.Equals("fr", StringComparison.OrdinalIgnoreCase);
        IsEnglish = !IsFrench;
    }

    [RelayCommand]
    private void Refresh()
    {
        Downloads.Refresh();

        // Actualiser used to be silent, which made F5 indistinguishable from a key that does not
        // work; the status bar now confirms it.
        Report("Status_Refreshed");
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void FindNext()
    {
        if (!Downloads.FindNext()) Report("Search_NoMatch");
    }

    [RelayCommand]
    private void SelectAll() => SelectAllRequested?.Invoke(this, EventArgs.Empty);

    // ---- tray counters and completion -----------------------------------------

    private void OnStoreRefreshed()
    {
        var active = _store.Items.Where(item => item.Status == DownloadStatus.Downloading).ToArray();
        Tray.SetSummary(active.Length, active.Sum(item => item.SpeedBps));

        if (_settings.Current.NotifyOnComplete)
        {
            foreach (var item in _store.Items)
            {
                if (item.Status != DownloadStatus.Completed) continue;
                if (!_completedAnnounced.Add(item.Id)) continue;

                // Rows seeded from the archive were already complete before this run.
                if (item.IsRestored) continue;
                Tray.NotifyCompletion(item.FileName);
            }
        }
    }

    protected override void OnLanguageChanged()
    {
        RefreshLanguageFlags();
        if (_statusKey != null) StatusMessage = Localizer.Get(_statusKey);
    }

    // ---- exit -----------------------------------------------------------------

    /// <summary>"Quitter" from the menu or the tray: one confirmation when bytes are still moving.</summary>
    [RelayCommand]
    private void Exit()
    {
        var unfinished = _store.Items.Count(item => item.IsOngoing);
        if (unfinished > 0)
        {
            var answer = MessageBox.Show(
                Localizer.Format("Dlg_ExitActive", unfinished),
                Localizer.Get("Dlg_ExitTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;
        }

        try { _store.PersistIfDirtyNow(); } catch { }
        Tray.Dispose();
        Application.Current?.Shutdown(0);
    }

    /// <summary>Called from the window's Closing handler: the list on disk matches the list on screen.</summary>
    public void FlushArchive()
    {
        try { _store.PersistIfDirtyNow(); } catch { }
    }

    /// <summary>
    /// Options → "Réduire dans la zone de notification au lieu de fermer": when it is unticked the
    /// minimize button leaves the window in the taskbar instead of parking it in the tray.
    /// </summary>
    public bool MinimizesToTray => _settings.Current.MinimizeToTray;
}
