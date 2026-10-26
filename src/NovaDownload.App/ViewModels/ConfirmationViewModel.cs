using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.App.Views;
using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

/// <summary>One entry of the category combo: a built-in label or a user-created name.</summary>
public sealed record CategoryOption(string Label, DownloadCategory Value, string Custom)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Confirmation de téléchargement": where it goes, under which category, and whether it starts now
/// or waits in the queue. The size is probed for real — a HEAD request for a server, the metadata
/// for a .torrent — and shows a dash when the remote will not say.
/// </summary>
public partial class ConfirmationViewModel : LocalizedViewModel
{
    private readonly IDownloadService _service;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;

    [ObservableProperty] private string source = string.Empty;
    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private string folder = string.Empty;
    [ObservableProperty] private string description = string.Empty;
    [ObservableProperty] private string sizeText = string.Empty;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool startLater;
    [ObservableProperty] private CategoryOption? selectedCategory;
    [ObservableProperty] private ObservableCollection<CategoryOption> categories = new();

    public ConfirmationViewModel(IDownloadService service, ISettingsService settings, HttpClient http)
    {
        _service = service;
        _settings = settings;
        _http = http;
        sizeText = Localizer.Get("Common_Dash");
    }

    /// <summary>Row created by the last <see cref="CommitAsync"/>, so the shell can select it.</summary>
    public Guid? StartedId { get; private set; }

    public string CustomCategory => SelectedCategory?.Custom ?? string.Empty;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The "+" line: a name the user typed, filed under its own pane node.</summary>
    public bool IsCustomSelected => CustomCategory.Length > 0;

    /// <summary>Torrents get a different hint: their bytes come from a swarm, not a server.</summary>
    public string KindHint => TorrentSourceRules.IsTorrent(Source)
        ? Localizer.Get("Confirm_TorrentHint")
        : Localizer.Get("Confirm_HttpHint");

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedCategoryChanged(CategoryOption? value)
    {
        OnPropertyChanged(nameof(CustomCategory));
        OnPropertyChanged(nameof(IsCustomSelected));

        // A category with its own folder decides where the file lands, unless the user typed one.
        if (value is { Value: not DownloadCategory.Other } && !IsCustomSelected &&
            _settings.Current.CategoryFolders.TryGetValue(value.Value.ToString(), out var folder) &&
            !string.IsNullOrWhiteSpace(folder))
        {
            Folder = folder;
        }
    }

    protected override void OnLanguageChanged()
    {
        RebuildCategories();
        OnPropertyChanged(nameof(KindHint));
    }

    /// <summary>Prepares the dialog for one source, using the folder chosen in the previous step.</summary>
    public async Task InitializeAsync(string source, string folder)
    {
        Source = source;
        FileName = AddUrlViewModel.GuessFileName(source);
        Folder = string.IsNullOrWhiteSpace(folder) ? _settings.Current.DefaultDownloadDirectory : folder;

        RebuildCategories();

        var isTorrent = TorrentSourceRules.IsTorrent(source);
        var category = DownloadClassifier.Classify(FileName, isTorrent);
        SelectedCategory = Categories.FirstOrDefault(option => option.Value == category && option.Custom.Length == 0)
                           ?? Categories[0];

        await ProbeSizeAsync();
    }

    private void RebuildCategories()
    {
        var previous = SelectedCategory?.Custom.Length > 0
            ? SelectedCategory.Custom
            : null;

        var options = new List<CategoryOption>();
        foreach (var category in Enum.GetValues<DownloadCategory>())
            options.Add(new CategoryOption(DownloadClassifier.Text(category), category, string.Empty));

        foreach (var custom in _settings.Current.CustomCategories)
        {
            if (!options.Any(option => string.Equals(option.Custom, custom, StringComparison.OrdinalIgnoreCase)))
                options.Add(new CategoryOption(custom, DownloadCategory.Other, custom));
        }

        Categories = new ObservableCollection<CategoryOption>(options);

        SelectedCategory = previous != null
            ? Categories.FirstOrDefault(option => string.Equals(option.Custom, previous, StringComparison.OrdinalIgnoreCase))
            : Categories.FirstOrDefault(option => option.Value == (SelectedCategory?.Value ?? DownloadCategory.Other));
    }

    /// <summary>Files a new custom category under its own node and selects it.</summary>
    public void AddCustomCategory(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;

        if (!_settings.Current.CustomCategories.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Current.CustomCategories.Add(trimmed);
            _ = _settings.SaveAsync();
        }

        RebuildCategories();
        SelectedCategory = Categories.FirstOrDefault(option =>
            string.Equals(option.Custom, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// HEAD for a server, torrent metadata for a .torrent file. A magnet is left at "—": its size
    /// only exists once a peer answers, and waiting for that here would freeze the dialog.
    /// </summary>
    private async Task ProbeSizeAsync()
    {
        if (TorrentSourceRules.IsMagnet(Source)) return;

        try
        {
            if (TorrentSourceRules.IsTorrent(Source))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var files = await _service.LoadTorrentMetadataAsync(Source, cts.Token);
                var total = files.Sum(file => file.Length);
                if (total > 0) SizeText = Formatters.Bytes(total);
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Head, Source);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var length = response.Content.Headers.ContentLength;
            if (length is > 0) SizeText = Formatters.Bytes(length.Value);
        }
        catch (Exception)
        {
            // A server that refuses HEAD or a torrent that needs the swarm: the dash stays.
        }
    }

    /// <summary>Starts the row. Returns false when the user cancelled the file picker.</summary>
    public async Task<bool> CommitAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        Error = null;

        try
        {
            var source = Source.Trim();

            // A .torrent carries its file list: the picker opens before anything is queued.
            if (TorrentSourceRules.IsTorrent(source) && !TorrentSourceRules.IsMagnet(source))
            {
                var files = await _service.LoadTorrentMetadataAsync(source);
                if (files.Count > 1)
                {
                    // Téléchargement direct de tous les fichiers, sans fenêtre de sélection.
                    Directory.CreateDirectory(Folder);
                    var picked = await _service.StartDownloadAsync(source, Folder, files, FileName);
                    await ApplyPreferencesAsync(picked);
                    return true;
                }
            }

            Directory.CreateDirectory(Folder);
            var item = await _service.StartDownloadAsync(source, Folder, FileName);
            await ApplyPreferencesAsync(item);
            return true;
        }
        catch (Exception ex)
        {
            // Engine failures are network text, not catalog text: shown verbatim.
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyPreferencesAsync(DownloadItem item)
    {
        item.Description = Description.Trim();
        item.CustomCategory = CustomCategory;
        if (SelectedCategory is { } option && option.Custom.Length == 0) item.Category = option.Value;

        StartedId = item.Id;

        // "Plus tard": the row exists and holds its place, but nothing moves until the user says so.
        if (StartLater) await _service.PauseAsync(item.Id);
    }
}
