using System.Text.Json;
using System.Text.RegularExpressions;
using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.UnitTests.Browser;

/// <summary>
/// Cross-file invariants that no single project can check on its own: the installer, the C#
/// constants, the extension manifests and the two language catalogs must agree.
/// </summary>
public class PackagingAndConfigurationTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "NovaDownload.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string RepoPath(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    [Fact]
    public void The_Installer_AppId_Matches_The_Application_Constant()
    {
        var script = File.ReadAllText(RepoPath("installer", "NovaDownload.iss"));
        var match = Regex.Match(script, "#define AppId \"\\{\\{([0-9A-Fa-f-]{36})\\}\"");

        Assert.True(match.Success, "installer/NovaDownload.iss does not define AppId");
        Assert.Equal(BrowserIntegrationInfo.InstallerAppId.Trim('{', '}'), match.Groups[1].Value);
    }

    [Fact]
    public void The_Uninstall_Key_Follows_Inno_Setup_Convention()
    {
        Assert.Equal(BrowserIntegrationInfo.InstallerAppId + "_is1", BrowserIntegrationInfo.InstallerUninstallKey);
        Assert.StartsWith("{", BrowserIntegrationInfo.InstallerUninstallKey);
    }

    [Fact]
    public void The_Installer_Registers_The_Host_And_Never_Deletes_User_Data()
    {
        var script = File.ReadAllText(RepoPath("installer", "NovaDownload.iss"));

        Assert.Contains("--register", script);
        Assert.Contains("--unregister", script);
        Assert.Contains("uninsneveruninstall", script);
        Assert.Contains("desktopicon", script);
        Assert.Contains("novadownload", script);
        Assert.Contains("{group}", script);

        // Nothing may point an uninstall-time deletion at the profile data folders.
        Assert.DoesNotContain("[UninstallDelete]", script);
    }

    [Fact]
    public void The_Installer_Ships_No_Prerequisite_And_Supports_Per_User_Install()
    {
        var script = File.ReadAllText(RepoPath("installer", "NovaDownload.iss"));

        Assert.Contains("PrivilegesRequiredOverridesAllowed", script);
        Assert.Contains("self-contained", File.ReadAllText(RepoPath("installer", "build-release.ps1")),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet-runtime", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Extension_Identity_File_Matches_The_Application_Constants()
    {
        var path = RepoPath("browser-extensions", "extension-identity.json");
        Assert.True(File.Exists(path), $"missing {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(BrowserIntegrationInfo.HostName, root.GetProperty("hostName").GetString());
        Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionKey, root.GetProperty("chromiumExtensionKey").GetString());
        Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionId, root.GetProperty("chromiumExtensionId").GetString());
        Assert.Equal(BrowserIntegrationInfo.FirefoxExtensionId, root.GetProperty("firefoxExtensionId").GetString());
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("edge")]
    public void The_Chromium_Manifests_Carry_The_Real_Key_And_The_Host_Can_Reach_Them(string browser)
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("browser-extensions", browser, "manifest.json"))).RootElement;

        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        Assert.Equal("__MSG_extName__", manifest.GetProperty("name").GetString());
        Assert.Equal("background.js", manifest.GetProperty("background").GetProperty("service_worker").GetString());
        Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionKey, manifest.GetProperty("key").GetString());
        Assert.False(manifest.TryGetProperty("host_permissions", out _),
            "host permissions must stay optional so nothing is injected by default");
    }

    [Fact]
    public void The_Firefox_Manifest_Uses_The_Gecko_Identity()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("browser-extensions", "firefox", "manifest.json"))).RootElement;
        var gecko = manifest.GetProperty("browser_specific_settings").GetProperty("gecko");

        Assert.Equal(BrowserIntegrationInfo.FirefoxExtensionId, gecko.GetProperty("id").GetString());
        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
    }

    [Fact]
    public void Both_Language_Catalogs_Stay_In_Parity()
    {
        var english = Localizer.Catalog("en");
        var french = Localizer.Catalog("fr");
        var missingInFr = english.Keys.Except(french.Keys, StringComparer.Ordinal).ToArray();
        var missingInEn = french.Keys.Except(english.Keys, StringComparer.Ordinal).ToArray();

        Assert.True(missingInFr.Length == 0, "missing French strings: " + string.Join(", ", missingInFr));
        Assert.True(missingInEn.Length == 0, "missing English strings: " + string.Join(", ", missingInEn));
    }

    [Fact]
    public void The_Browser_Integration_Settings_Exist_In_Both_Languages()
    {
        var keys = new[]
        {
            "Set_BrowserTitle", "Set_BrowserSubtitle", "Set_BrowserEnable", "Set_BrowserActivate",
            "Set_BrowserStatusLabel", "Set_BrowserStatusReady", "Set_BrowserStatusIdle",
            "Set_BrowserRegisteredFor", "Set_BrowserNone", "Set_BrowserHostMissing", "Set_BrowserIds",
            "Set_BrowserRegistered", "Set_BrowserRegistrationFailed", "Btn_RegisterBrowser"
        };

        var english = Localizer.Catalog("en");
        var french = Localizer.Catalog("fr");

        foreach (var key in keys)
        {
            Assert.True(english.ContainsKey(key), $"missing English string '{key}'");
            Assert.True(french.ContainsKey(key), $"missing French string '{key}'");
        }
    }

    [Fact]
    public void Browser_Integration_Is_On_By_Default_Without_Asking_For_Permissions()
    {
        var settings = new AppSettings();

        Assert.True(settings.EnableBrowserIntegration);
        Assert.True(settings.ActivateOnBrowserDownload);
        Assert.False(string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory));
    }

    [Fact]
    public void The_Protocol_Version_Is_The_One_The_Extensions_Speak()
    {
        var extensionRoot = RepoPath("browser-extensions", "common", "lib");
        var match = Regex.Match(File.ReadAllText(Path.Combine(extensionRoot, "compat.js")),
            "PROTOCOL_VERSION\\s*=\\s*(\\d+)");

        Assert.True(match.Success, "the extension never declares PROTOCOL_VERSION");
        Assert.Equal(BrowserActions.ProtocolVersion.ToString(), match.Groups[1].Value);
        Assert.Contains("PROTOCOL_VERSION", File.ReadAllText(Path.Combine(extensionRoot, "native.js")));
    }

    [Fact]
    public void The_Pipe_Is_Per_User_And_Local()
    {
        var name = BrowserBridgeProtocol.ResolvePipeName();

        Assert.StartsWith(BrowserBridgeProtocol.PipeNamePrefix, name);
        Assert.DoesNotContain("\\", name);
        Assert.DoesNotContain("/", name);
        Assert.True(name.Length <= 200, $"pipe name too long: {name.Length}");
        Assert.Matches("^[A-Za-z0-9._-]+$", name);
        Assert.Contains(".", name[BrowserBridgeProtocol.PipeNamePrefix.Length..]);
    }

    [Fact]
    public void The_Protocol_Never_Exposes_A_Network_Port()
    {
        var source = File.ReadAllText(RepoPath("src", "NovaDownload.BrowserIntegration", "Protocol", "BrowserBridgeProtocol.cs"));

        Assert.DoesNotContain("TcpListener", source);
        Assert.DoesNotContain("HttpListener", source);
        Assert.Contains("NamedPipeServerStream", source);
        Assert.Contains("CurrentUserOnly", source);
    }
}
