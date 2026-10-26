using NovaDownload.Infrastructure.Services;

namespace NovaDownload.UnitTests.Infrastructure;

/// <summary>
/// The settings store must be redirectable: the end-to-end verification launches the real
/// application and cannot touch the developer's own profile.
/// </summary>
public class JsonSettingsServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void An_Explicit_Directory_Wins_Over_Everything()
    {
        var dir = TempDir();

        Assert.Equal(Path.GetFullPath(dir), JsonSettingsService.ResolveDirectory(dir));
    }

    [Fact]
    public void The_Environment_Variable_Redirects_The_Folder()
    {
        var dir = TempDir();
        var previous = Environment.GetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable, dir);

            Assert.Equal(Path.GetFullPath(dir), JsonSettingsService.ResolveDirectory());

            var service = new JsonSettingsService();
            Assert.Equal(Path.GetFullPath(dir), service.DirectoryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable, previous);
        }
    }

    [Fact]
    public void Without_An_Override_It_Uses_The_Roaming_Profile()
    {
        var previous = Environment.GetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable, null);

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaDownload");

            Assert.Equal(expected, JsonSettingsService.ResolveDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(JsonSettingsService.DataDirectoryVariable, previous);
        }
    }

    [Fact]
    public async Task A_Fresh_Folder_Starts_With_Browser_Integration_Enabled()
    {
        var dir = TempDir();
        var service = new JsonSettingsService(dir);

        await service.LoadAsync();

        Assert.True(service.Current.EnableBrowserIntegration);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public async Task Saved_Settings_Are_Read_Back()
    {
        var dir = TempDir();
        var writer = new JsonSettingsService(dir);
        await writer.LoadAsync();

        writer.Current.EnableBrowserIntegration = false;
        writer.Current.MaxConcurrentDownloads = 7;
        await writer.SaveAsync();

        var reader = new JsonSettingsService(dir);
        await reader.LoadAsync();

        Assert.False(reader.Current.EnableBrowserIntegration);
        Assert.Equal(7, reader.Current.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task A_Corrupt_File_Falls_Back_To_Defaults_Instead_Of_Crashing()
    {
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{ not json");

        var service = new JsonSettingsService(dir);
        await service.LoadAsync();

        Assert.True(service.Current.EnableBrowserIntegration);
    }
}
