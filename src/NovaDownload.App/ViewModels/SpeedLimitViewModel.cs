using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Services;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// "Limiteur de vitesse" (F9): one ceiling for both engines. The value is pushed into the running
/// limiter, so transfers already in flight slow down before the dialog closes.
/// </summary>
public partial class SpeedLimitViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;
    private readonly HttpDownloadService? _http;
    private readonly TorrentDownloadService? _torrent;

    public SpeedLimitViewModel(ISettingsService settings, HttpDownloadService? http,
        TorrentDownloadService? torrent)
    {
        _settings = settings;
        _http = http;
        _torrent = torrent;

        var configured = settings.Current.SpeedLimitKib;
        unlimited = configured <= 0;
        kib = configured > 0 ? configured : 512;
    }

    [ObservableProperty] private bool unlimited = true;
    [ObservableProperty] private int kib = 512;

    /// <summary>Current ceiling in the user's own units, shown under the field.</summary>
    public string CurrentText => _settings.Current.SpeedLimitKib <= 0
        ? Localizer.Get("Speed_CurrentUnlimited")
        : Localizer.Format("Speed_Current", Core.Common.Formatters.Speed(_settings.Current.SpeedLimitKib * 1024.0));

    public string Summary => _settings.Current.SpeedLimitKib <= 0
        ? Localizer.Get("Speed_AppliedUnlimited")
        : Localizer.Format("Speed_Applied", Core.Common.Formatters.Speed(_settings.Current.SpeedLimitKib * 1024.0));

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Writes the setting and pushes it live. Called once the dialog is confirmed.</summary>
    public void Apply()
    {
        var value = Unlimited ? 0 : Math.Clamp(Kib, 1, 1_000_000);

        _settings.Current.SpeedLimitKib = value;
        _settings.Current.TorrentMaxDownloadKib = value;
        _ = _settings.SaveAsync();

        if (_http != null) _http.GlobalSpeedLimitBps = (long)value * 1024;

        if (_torrent != null)
        {
            var current = _settings.Current;
            _ = _torrent.ApplyOptionsAsync(new TorrentEngineOptions
            {
                ListenPort = current.TorrentListenPort,
                EnableDht = current.TorrentEnableDht,
                EnablePeerExchange = current.TorrentEnablePex,
                EnableLocalPeerDiscovery = current.TorrentEnableLsd,
                EnablePortForwarding = current.TorrentEnablePortForwarding,
                SeedAfterCompletion = current.TorrentSeedAfterCompletion,
                MaxUploadKib = current.TorrentMaxUploadKib,
                MaxDownloadKib = value,
                ConnectionsPerTorrent = current.TorrentConnectionsPerTorrent,
                MaxConnections = current.TorrentMaxConnections
            });
        }

        OnPropertyChanged(nameof(CurrentText));
    }
}
