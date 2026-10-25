using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaDownload.App.Services;
using NovaDownload.App.ViewModels;
using NovaDownload.App.Views;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Registration;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;
using NovaDownload.DownloadEngine.Torrenting;
using NovaDownload.Infrastructure.Services;

namespace NovaDownload.App;

public partial class App : Application
{
    public static IHost? AppHost { get; private set; }

    public App()
    {
        AppHost = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromHours(1) });

                services.AddSingleton<ISettingsService, JsonSettingsService>();
                services.AddSingleton<ThemeService>();

                services.AddSingleton(sp =>
                {
                    var settings = sp.GetRequiredService<ISettingsService>();
                    return new HttpDownloadService(
                        sp.GetRequiredService<HttpClient>(),
                        settings.Current.MaxConcurrentDownloads);
                });

                services.AddSingleton(sp =>
                {
                    var settings = sp.GetRequiredService<ISettingsService>();
                    return new TorrentDownloadService(
                        sp.GetRequiredService<HttpClient>(),
                        TorrentOptionsFrom(settings.Current),
                        message => LogLine("torrent", message));
                });

                // The router is the only IDownloadService anyone resolves: it picks the engine from
                // the source and merges both queues into one snapshot.
                services.AddSingleton<IDownloadService>(sp => new DownloadServiceRouter(
                    sp.GetRequiredService<HttpDownloadService>(),
                    sp.GetRequiredService<TorrentDownloadService>()));

                services.AddSingleton(sp => new BrowserIntegrationService(
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<ISettingsService>(),
                    message => LogLine("browser-bridge", message)));

                services.AddSingleton<DownloadStore>();

                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<DownloadsViewModel>();
                services.AddSingleton<AddDownloadViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindowViewModel>();

                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log("dispatcher", args.Exception);
        };

        var launchRequest = StartupRequest.Parse(e.Args);

        try
        {
            // A second launch (typically a novadownload:// link) hands the request to the running
            // instance instead of opening a duplicate window.
            if (launchRequest != null)
            {
                var (forwarded, response) = await launchRequest.TryForwardAsync();
                if (forwarded)
                {
                    LogLine("startup", $"forwarded '{launchRequest.Source}' to the running instance: {response?.Code}");
                    Shutdown(0);
                    return;
                }
            }

            await AppHost!.StartAsync();

            var settings = AppHost.Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();

            // The UI language must be settled before the window (and its bindings) exist.
            Localizer.Apply(settings.Current.Language);
            AppHost.Services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);

            // magnet: must already point here before the window opens: a link clicked while the app
            // is starting has to land in this process, not in a second one.
            ApplyMagnetHandler(settings.Current);

            var bridge = AppHost.Services.GetRequiredService<BrowserIntegrationService>();

            var window = AppHost.Services.GetRequiredService<MainWindow>();
            var windowModel = AppHost.Services.GetRequiredService<MainWindowViewModel>();

            bridge.RequestAccepted += response => OnBrowserRequestAccepted(window, windowModel, response);
            bridge.Start();

            window.Show();

            if (launchRequest != null)
            {
                var response = await bridge.HandleStartupRequestAsync(launchRequest);
                LogLine("startup", $"'{launchRequest.Source}' → {response.Code}: {response.Message}");
                if (!response.Ok && response.Code != ResponseCodes.Duplicate)
                {
                    MessageBox.Show(
                        $"{response.Message}{Environment.NewLine}{Environment.NewLine}{launchRequest.Url}",
                        "NovaDownload", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

        }
        catch (Exception ex)
        {
            Log("startup", ex);
            MessageBox.Show(ex.ToString(), "NovaDownload — startup failure");
            Shutdown(1);
        }
    }

    /// <summary>
    /// A validated browser download reached the engine: bring the window forward (when the user
    /// asked for it) and show the queue so the live progress is actually visible.
    /// </summary>
    private void OnBrowserRequestAccepted(MainWindow window, MainWindowViewModel model, NovaDownload.BrowserIntegration.Models.BrowserResponse response)
    {
        try
        {
            window.Dispatcher.BeginInvoke(() =>
            {
                var settings = AppHost?.Services.GetService<ISettingsService>();
                model.ShowDownloadsCommand.Execute(null);

                if (settings?.Current.ActivateOnBrowserDownload != true) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                if (!window.IsVisible) window.Show();
                window.Activate();
            });
        }
        catch (Exception ex)
        {
            Log("bridge-activate", ex);
        }
    }

    private static void Log(string stage, Exception? ex) =>
        Write(LogDirectory("startup.log"), stage, ex?.ToString() ?? string.Empty);

    internal static void LogLine(string stage, string message) =>
        Write(LogDirectory(Path.Combine("logs", "novadownload.log")), stage, message);

    private static string LogDirectory(string fileName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaDownload");
        var full = Path.Combine(dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? dir);
        return full;
    }

    /// <summary>Maps the persisted preferences onto the BitTorrent session.</summary>
    private static TorrentEngineOptions TorrentOptionsFrom(AppSettings settings) => new()
    {
        ListenPort = settings.TorrentListenPort,
        EnableDht = settings.TorrentEnableDht,
        EnablePeerExchange = settings.TorrentEnablePex,
        EnableLocalPeerDiscovery = settings.TorrentEnableLsd,
        EnablePortForwarding = settings.TorrentEnablePortForwarding,
        SeedAfterCompletion = settings.TorrentSeedAfterCompletion,
        MaxUploadKib = settings.TorrentMaxUploadKib,
        MaxDownloadKib = settings.TorrentMaxDownloadKib,
        ConnectionsPerTorrent = settings.TorrentConnectionsPerTorrent,
        MaxConnections = settings.TorrentMaxConnections,
        CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaDownload", "torrent-cache")
    };

    /// <summary>
    /// Applies the <c>magnet:</c> preference to the registry. Called at startup and again whenever
    /// the setting changes, so a magnet link clicked a second later already reaches this process.
    /// </summary>
    internal static void ApplyMagnetHandler(AppSettings settings)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return;

            if (settings.TorrentHandleMagnetLinks)
            {
                UrlProtocolRegistrar.RegisterMagnet(executable);
                LogLine("magnet-protocol", $"registered for {executable}");
            }
            else
            {
                UrlProtocolRegistrar.UnregisterMagnet();
                LogLine("magnet-protocol", "unregistered");
            }
        }
        catch (Exception ex)
        {
            Log("magnet-protocol", ex);
        }
    }

    private static void Write(string path, string stage, string message)
    {
        try
        {
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var bridge = AppHost?.Services.GetService<BrowserIntegrationService>();
            if (bridge != null) await bridge.StopAsync();

            // Saves the DHT routing table and flushes fast-resume data before the session dies.
            var torrent = AppHost?.Services.GetService<TorrentDownloadService>();
            if (torrent != null) await torrent.DisposeAsync();

            var settings = AppHost?.Services.GetService<ISettingsService>();
            if (settings != null) await settings.SaveAsync();
            if (AppHost != null) await AppHost.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
        base.OnExit(e);
    }
}
