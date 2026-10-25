using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

public partial class AddDownloadViewModel : LocalizedViewModel
{
    private readonly IDownloadService _downloadService;
    private readonly ISettingsService _settings;
    private string? _errorKey;

    public event EventHandler? DownloadStarted;

    [ObservableProperty] private string url = string.Empty;
    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private string destinationDirectory = string.Empty;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool isBusy;

    public AddDownloadViewModel(IDownloadService downloadService, ISettingsService settings)
    {
        _downloadService = downloadService;
        _settings = settings;
        DestinationDirectory = settings.Current.DefaultDownloadDirectory;
    }

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(Url) && IsValidSource(Url);

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True while the field holds a torrent source, so the view can explain the swarm behaviour.</summary>
    public bool IsTorrentSource => TorrentSource.IsTorrent(Url);

    public string SourceHint => IsTorrentSource
        ? Localizer.Get("Add_TorrentHint")
        : Localizer.Get("Add_FileNameHint");

    /// <summary>Bottom-of-form note: the resume story is not the same for a swarm as for a server.</summary>
    public string ResumeHint => IsTorrentSource
        ? Localizer.Get("Add_TorrentResumeHint")
        : Localizer.Get("Add_ResumeHint");

    partial void OnUrlChanged(string value)
    {
        SetError(null);
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsTorrentSource));
        OnPropertyChanged(nameof(SourceHint));
        OnPropertyChanged(nameof(ResumeHint));
    }

    protected override void OnLanguageChanged()
    {
        if (_errorKey != null) Error = Localizer.Get(_errorKey);
        OnPropertyChanged(nameof(SourceHint));
        OnPropertyChanged(nameof(ResumeHint));
    }

    /// <summary>Errors raised from the catalog are stored by key so they follow the language.</summary>
    private void SetError(string? key)
    {
        _errorKey = key;
        Error = key == null ? null : Localizer.Get(key);
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsText()) Url = Clipboard.GetText().Trim();
        }
        catch { }
    }

    /// <summary>Picks a local .torrent file and uses its path as the source.</summary>
    [RelayCommand]
    private void BrowseTorrent()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.Get("Dlg_OpenTorrent"),
            Filter = Localizer.Get("Dlg_TorrentFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true) Url = dialog.FileName;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DestinationDirectory };
        if (dialog.ShowDialog() == true) DestinationDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private async Task StartDownload()
    {
        if (IsBusy) return;

        if (!IsValidSource(Url))
        {
            SetError("Error_InvalidSource");
            return;
        }

        IsBusy = true;
        SetError(null);
        try
        {
            Directory.CreateDirectory(DestinationDirectory);
            var name = string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim();
            await _downloadService.StartDownloadAsync(Url.Trim(), DestinationDirectory, name);
            Url = string.Empty;
            FileName = string.Empty;
            DownloadStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Engine failures come from the network layer verbatim: they are not catalog text.
            _errorKey = null;
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SyncDestinationWithSettings()
    {
        if (string.IsNullOrWhiteSpace(DestinationDirectory))
            DestinationDirectory = _settings.Current.DefaultDownloadDirectory;
    }

    /// <summary>
    /// The field accepts three shapes: an absolute http/https link, a magnet URI, or the path of an
    /// existing .torrent file. Anything else is refused before the engine is touched.
    /// </summary>
    private static bool IsValidSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var source = value.Trim();

        if (TorrentSource.IsTorrent(source)) return true;

        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrWhiteSpace(uri.Host);
    }
}
