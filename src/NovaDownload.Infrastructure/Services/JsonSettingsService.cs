using System.Text.Json;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;

namespace NovaDownload.Infrastructure.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public JsonSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaDownload");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
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
