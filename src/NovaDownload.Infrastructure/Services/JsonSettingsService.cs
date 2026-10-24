using System.Text.Json;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;

namespace NovaDownload.Infrastructure.Services;

public class JsonSettingsService : ISettingsService
{
    /// <summary>
    /// Environment override for the folder holding <c>settings.json</c>. The end-to-end verification
    /// and any automated run point it at a throw-away folder so they never read or write the real
    /// user profile.
    /// </summary>
    public const string DataDirectoryVariable = "NOVADOWNLOAD_DATA_DIR";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    /// <summary>Folder the settings file lives in.</summary>
    public string DirectoryPath { get; }

    public JsonSettingsService() : this(null)
    {
    }

    public JsonSettingsService(string? directory)
    {
        DirectoryPath = ResolveDirectory(directory);
        System.IO.Directory.CreateDirectory(DirectoryPath);
        _path = Path.Combine(DirectoryPath, "settings.json");
    }

    /// <summary>Explicit folder, else <see cref="DataDirectoryVariable"/>, else the roaming profile.</summary>
    public static string ResolveDirectory(string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return Path.GetFullPath(explicitDirectory);

        var forced = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(forced))
            return Path.GetFullPath(forced);

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaDownload");
    }

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null) Current = loaded;
            }
            else
            {
                Current = new AppSettings();
                Directory.CreateDirectory(Current.DefaultDownloadDirectory);
                await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(Current, Options));
            }
        }
        catch
        {
            Current = new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(Current, Options));
        }
        catch { }
        finally
        {
            _gate.Release();
        }
    }
}
