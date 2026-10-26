using System.Diagnostics;
using System.Text;
using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Server;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Models;
using NovaDownload.DownloadEngine.Services;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// Drives the real <c>NovaDownload.NativeHost.exe</c> over stdin/stdout, exactly as a browser does:
/// 4-byte little-endian length prefix, then JSON. No mocks on the wire.
/// </summary>
public class NativeHostProcessTests
{
    private sealed class Settings : ISettingsService
    {
        public Settings(string directory) => Current = new AppSettings { DefaultDownloadDirectory = directory };
        public AppSettings Current { get; }
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "NovaDownload.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string HostExecutable()
    {
        var root = RepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(root, "src", "NovaDownload.NativeHost", "bin", "Release", "net9.0-windows", "NovaDownload.NativeHost.exe"),
            Path.Combine(root, "src", "NovaDownload.NativeHost", "bin", "Debug", "net9.0-windows", "NovaDownload.NativeHost.exe"),
            Path.Combine(root, "build", "app", "NovaDownload.NativeHost.exe")
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        throw new Xunit.Sdk.XunitException(
            "NovaDownload.NativeHost.exe was not found. Build the solution first. Looked in: " + string.Join(", ", candidates));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-host", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Starts the host exactly the way a browser would, with stdin/stdout as the wire.</summary>
    private static Process StartHost(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostExecutable(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(HostExecutable())!
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo) ?? throw new Xunit.Sdk.XunitException("could not start the native host");
        return process;
    }

    private static async Task SendAsync(Process process, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, BrowserBridgeProtocol.Json);
        await NativeMessageProtocol.WriteMessageAsync(process.StandardInput.BaseStream, json);
        await process.StandardInput.BaseStream.FlushAsync();
    }

    private static async Task<BrowserResponse> ReceiveAsync(Process process, int timeoutMs = 30000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var payload = await NativeMessageProtocol.ReadMessageAsync(process.StandardOutput.BaseStream, cts.Token);

        Assert.False(string.IsNullOrEmpty(payload), "the host closed stdout without answering");
        var response = System.Text.Json.JsonSerializer.Deserialize<BrowserResponse>(payload!, BrowserBridgeProtocol.Json);
        Assert.NotNull(response);
        return response!;
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public async Task The_Host_Reports_Unavailable_When_Nothing_Is_Listening()
    {
        var process = StartHost("--no-launch", "--pipe", "NovaDownload.Test." + Guid.NewGuid().ToString("n"));
        try
        {
            await SendAsync(process, new BrowserRequest { Action = BrowserActions.Ping });

            var response = await ReceiveAsync(process);

            Assert.False(response.Ok);
            Assert.Equal(ResponseCodes.AppUnavailable, response.Code);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task The_Host_Accepts_The_Arguments_Chromium_Appends()
    {
        // Chromium launches: host.exe <origin> --parent-window=<hwnd>
        var process = StartHost(
            $"chrome-extension://{BrowserIntegrationInfo.ChromiumExtensionId}/",
            "--parent-window=0",
            "--no-launch",
            "--pipe", "NovaDownload.Test." + Guid.NewGuid().ToString("n"));
        try
        {
            await SendAsync(process, new BrowserRequest { Action = BrowserActions.Ping });

            var response = await ReceiveAsync(process);

            // Reaching the relay at all proves the arguments were accepted: an unknown argument
            // makes the host print usage and exit before it reads stdin.
            Assert.False(response.Ok);
            Assert.Equal(ResponseCodes.AppUnavailable, response.Code);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task The_Host_Accepts_The_Arguments_Firefox_Appends()
    {
        // Firefox launches: host.exe <manifest.json path> <extension id>
        var process = StartHost(
            Path.Combine(Path.GetTempPath(), "com.novadownload.host.json"),
            BrowserIntegrationInfo.FirefoxExtensionId,
            "--no-launch",
            "--pipe", "NovaDownload.Test." + Guid.NewGuid().ToString("n"));
        try
        {
            await SendAsync(process, new BrowserRequest { Action = BrowserActions.Ping });

            var response = await ReceiveAsync(process);

            Assert.False(response.Ok);
            Assert.Equal(ResponseCodes.AppUnavailable, response.Code);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task The_Host_Relays_A_Download_To_The_Application_And_Back()
    {
        var payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        await using var server = new TestHttpServer(payload) { FileName = "relayed.mp4" };

        var directory = TempDir();
        var engine = new HttpDownloadService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, 2);
        var pipeName = "NovaDownload.Test." + Guid.NewGuid().ToString("n");
        var processor = new BrowserRequestProcessor(engine, new Settings(directory));
        await using var bridge = new BrowserBridgeServer(processor, pipeName);
        bridge.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               !await BrowserBridgeProtocol.IsServerAvailableAsync(pipeName, TimeSpan.FromMilliseconds(300)))
            await Task.Delay(100);

        var process = StartHost("--no-launch", "--pipe", pipeName);
        try
        {
            await SendAsync(process, new BrowserRequest
            {
                Action = BrowserActions.Download,
                Url = server.Url,
                FileName = "relayed.mp4",
                Browser = "firefox",
                RequestId = "host-e2e-1"
            });

            var response = await ReceiveAsync(process);

            Assert.True(response.Ok, response.Message);
            Assert.Equal(ResponseCodes.Ok, response.Code);
            Assert.Equal("relayed.mp4", response.FileName);

            var finish = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < finish &&
                   engine.Snapshot().FirstOrDefault()?.Status != DownloadStatus.Completed)
                await Task.Delay(50);

            var item = Assert.Single(engine.Snapshot());
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task Malformed_Json_Is_Answered_With_A_Protocol_Error()
    {
        var process = StartHost("--no-launch", "--pipe", "NovaDownload.Test." + Guid.NewGuid().ToString("n"));
        try
        {
            await NativeMessageProtocol.WriteMessageAsync(process.StandardInput.BaseStream, "{ this is not json");
            await process.StandardInput.BaseStream.FlushAsync();

            var response = await ReceiveAsync(process);

            Assert.False(response.Ok);
            Assert.Equal(ResponseCodes.MalformedMessage, response.Code);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public void Identity_Is_Machine_Readable_And_Matches_The_Constants()
    {
        var process = StartHost("--identity", "--json");
        try
        {
            var stdout = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(20000), "the host did not exit");
            Assert.Equal(0, process.ExitCode);

            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            var root = document.RootElement;

            Assert.Equal(BrowserIntegrationInfo.HostName, root.GetProperty("host").GetString());
            Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionId, root.GetProperty("chromiumId").GetString());
            Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionId, root.GetProperty("chromiumIdCalc").GetString());
            Assert.Equal(BrowserIntegrationInfo.FirefoxExtensionId, root.GetProperty("firefoxId").GetString());
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public void An_Unknown_Argument_Fails_With_The_Usage_Exit_Code()
    {
        var process = StartHost("--not-a-real-option");
        try
        {
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20000), "the host did not exit");
            Assert.Equal(2, process.ExitCode);
            Assert.Contains("Unknown argument", stderr);
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public void Status_Mode_Runs_And_Reports_The_Host_Name()
    {
        var process = StartHost("--status");
        try
        {
            var stdout = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(20000), "the host did not exit");
            Assert.Equal(0, process.ExitCode);
            Assert.Contains(BrowserIntegrationInfo.HostName, stdout);
            Assert.Contains("Pipe name", stdout);
        }
        finally
        {
            process.Dispose();
        }
    }
}
