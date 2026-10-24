using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;

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

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(Url) && IsValidUrl(Url);

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnUrlChanged(string value)
    {
        SetError(null);
        OnPropertyChanged(nameof(CanStart));
    }

    protected override void OnLanguageChanged()
    {
        if (_errorKey != null) Error = Localizer.Get(_errorKey);
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

        if (!IsValidUrl(Url))
        {
            SetError("Error_InvalidUrl");
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

    private static bool IsValidUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
