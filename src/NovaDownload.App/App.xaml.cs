using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaDownload.App.Services;
using NovaDownload.App.ViewModels;
using NovaDownload.App.Views;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;
using NovaDownload.DownloadEngine.Services;
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
                services.AddSingleton<IDownloadService>(sp => sp.GetRequiredService<HttpDownloadService>());

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

        try
        {
            await AppHost!.StartAsync();

            var settings = AppHost.Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();

            // The UI language must be settled before the window (and its bindings) exist.
            Localizer.Apply(settings.Current.Language);
            AppHost.Services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);

            var window = AppHost.Services.GetRequiredService<MainWindow>();
            window.Show();
        }
        catch (Exception ex)
        {
            Log("startup", ex);
            MessageBox.Show(ex.ToString(), "NovaDownload — startup failure");
            Shutdown(1);
        }
    }

    private static void Log(string stage, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaDownload");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {ex}{Environment.NewLine}");
        }
        catch { }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var settings = AppHost?.Services.GetService<ISettingsService>();
            if (settings != null) await settings.SaveAsync();
            if (AppHost != null) await AppHost.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
        base.OnExit(e);
    }
}
