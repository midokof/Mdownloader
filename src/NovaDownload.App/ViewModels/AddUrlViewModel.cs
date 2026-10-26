using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaDownload.Core.Common;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// "Ajouter une URL": one or more sources, one paste button, one torrent picker. The window only
/// validates; the confirmation dialog that follows decides the folder, the category and the start.
/// </summary>
public partial class AddUrlViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;
    private string? _errorKey;

    [ObservableProperty] private string urls = string.Empty;
    [ObservableProperty] private string destinationDirectory = string.Empty;
    [ObservableProperty] private string? error;

    public AddUrlViewModel(ISettingsService settings)
    {
        _settings = settings;
        destinationDirectory = settings.Current.DefaultDownloadDirectory;
    }

    /// <summary>Valid sources, one per non-empty line, filled by <see cref="TryAccept"/>.</summary>
    public IReadOnlyList<string> Accepted { get; private set; } = Array.Empty<string>();

    public bool HasError => !string.IsNullOrEmpty(Error);

    public void Reset(string? prefill)
    {
        Urls = prefill?.Trim() ?? string.Empty;
        DestinationDirectory = _settings.Current.DefaultDownloadDirectory;
        SetError(null);
        Accepted = Array.Empty<string>();
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnUrlsChanged(string value) => SetError(null);

    protected override void OnLanguageChanged()
    {
        if (_errorKey != null) Error = Localizer.Get(_errorKey);
    }

    private void SetError(string? key, params object?[] args)
    {
        _errorKey = key;
        Error = key == null ? null : Localizer.Format(key, args);
    }

    [RelayCommand]
    private void Paste()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Trim();
            Urls = string.IsNullOrEmpty(Urls) ? text : Urls.TrimEnd() + Environment.NewLine + text;
        }
        catch (Exception)
        {
            // Clipboard can be locked by another process: the field stays as it was.
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DestinationDirectory };
        if (dialog.ShowDialog() == true) DestinationDirectory = dialog.FolderName;
    }

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

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        Urls = string.IsNullOrEmpty(Urls) ? path : Urls.TrimEnd() + Environment.NewLine + path;
    }

    /// <summary>
    /// Splits the field, keeps the lines that are a real source, and reports the others. Nothing is
    /// silently dropped: the user sees which line was refused.
    /// </summary>
    public bool TryAccept()
    {
        var lines = Urls
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (lines.Length == 0)
        {
            SetError("Error_NoUrl");
            return false;
        }

        if (string.IsNullOrWhiteSpace(DestinationDirectory))
            DestinationDirectory = _settings.Current.DefaultDownloadDirectory;

        var accepted = new List<string>(lines.Length);
        var refused = new List<string>();

        foreach (var line in lines)
        {
            if (IsValidSource(line)) accepted.Add(line);
            else refused.Add(line);
        }

        if (refused.Count > 0)
        {
            SetError("Error_RefusedSources", refused.Count, refused[0]);
            return false;
        }

        Accepted = accepted;
        SetError(null);
        return accepted.Count > 0;
    }

    /// <summary>An absolute http(s) link, a magnet URI, or the path of an existing .torrent file.</summary>
    private static bool IsValidSource(string value)
    {
        if (TorrentSourceRules.IsTorrent(value)) return true;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>Folder the confirmation dialog starts from.</summary>
    public string Folder => string.IsNullOrWhiteSpace(DestinationDirectory)
        ? _settings.Current.DefaultDownloadDirectory
        : DestinationDirectory;

    public static string GuessFileName(string source)
    {
        if (TorrentSourceRules.IsTorrent(source)) return TorrentSource.DisplayName(source);

        try
        {
            var uri = new Uri(source);
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? Localizer.Get("Common_Untitled") : name;
        }
        catch (Exception)
        {
            return Localizer.Get("Common_Untitled");
        }
    }
}
