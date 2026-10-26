using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Registration;

namespace NovaDownload.NativeHost;

/// <summary>
/// Entry point of the native messaging host.
/// <list type="bullet">
///   <item>no arguments: speak the native messaging protocol on stdin/stdout (how browsers launch it);</item>
///   <item><c>--register</c> / <c>--unregister</c> / <c>--status</c>: manage the browser registration.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;

    private static string? _hostOverride;

    private static async Task<int> Main(string[] args)
    {
        var options = new HostOptions();
        string? mode = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--register":
                    mode = "register";
                    break;
                case "--unregister":
                    mode = "unregister";
                    break;
                case "--status":
                    mode = "status";
                    break;
                case "--identity":
                    mode = "identity";
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--no-launch":
                    options.AllowLaunch = false;
                    break;
                case "--app" when i + 1 < args.Length:
                    options.AppPath = args[++i];
                    break;
                case "--pipe" when i + 1 < args.Length:
                    options.PipeName = args[++i];
                    break;
                case "--manifest-dir" when i + 1 < args.Length:
                    options.ManifestDirectory = args[++i];
                    break;
                case "--host" when i + 1 < args.Length:
                    _hostOverride = args[++i];
                    break;
                case "--parent-window" when i + 1 < args.Length:
                    // Chromium appends the owning window handle; nothing here acts on it.
                    i++;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return ExitOk;
                default:
                    if (TryAcceptBrowserArgument(args[i], options)) break;
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return ExitUsage;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.AppPath))
            Environment.SetEnvironmentVariable(AppLocator.AppPathVariable, options.AppPath);

        if (mode != null) return RunAdministrativeMode(mode, options);

        var runner = new NativeHostRunner(options);
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        await runner.RunAsync(stdin, stdout).ConfigureAwait(false);
        return ExitOk;
    }

    private static int RunAdministrativeMode(string mode, HostOptions options)
    {
        var hostExecutable = _hostOverride
                             ?? Environment.ProcessPath
                             ?? Path.Combine(AppContext.BaseDirectory, "NovaDownload.NativeHost.exe");

        try
        {
            switch (mode)
            {
                case "register":
                {
                    var report = NativeHostRegistrar.Register(hostExecutable, options.ManifestDirectory);
                    Console.WriteLine($"Native messaging host '{BrowserIntegrationInfo.HostName}' registered.");
                    Console.WriteLine($"  host executable : {report.HostExecutable}");
                    Console.WriteLine($"  manifests       : {report.ManifestDirectory}");
                    Console.WriteLine($"  extension id    : {BrowserIntegrationInfo.ChromiumExtensionId} (Chromium)");
                    Console.WriteLine($"                    {BrowserIntegrationInfo.FirefoxExtensionId} (Firefox)");
                    foreach (var entry in report.Entries)
                    {
                        Console.WriteLine($"  {(entry.Registered ? "OK  " : "FAIL")}  {entry.DisplayName,-16} " +
                                          $"{(string.IsNullOrEmpty(entry.BrowserVersion) ? "(not installed)" : entry.BrowserVersion)} — {entry.Detail}");
                    }
                    return report.AnyRegistered ? ExitOk : ExitError;
                }
                case "unregister":
                {
                    var report = NativeHostRegistrar.Unregister(options.ManifestDirectory);
                    Console.WriteLine("Native messaging host registration removed.");
                    foreach (var entry in report.Entries)
                        Console.WriteLine($"  {(entry.Registered ? "FAIL" : "OK  ")}  {entry.DisplayName,-16} {entry.Detail}");
                    return ExitOk;
                }
                case "status":
                {
                    var report = NativeHostRegistrar.Inspect(options.ManifestDirectory, hostExecutable);
                    Console.WriteLine($"Host name     : {BrowserIntegrationInfo.HostName}");
                    Console.WriteLine($"Pipe name     : {BrowserBridgeProtocol.ResolvePipeName()}");
                    Console.WriteLine($"Application   : {AppLocator.TryResolve(out var source) ?? "(not found)"} [{source}]");
                    foreach (var entry in report.Entries)
                    {
                        Console.WriteLine($"  {(entry.Registered ? "registered" : "absent    ")}  {entry.DisplayName,-16} " +
                                          $"{(string.IsNullOrEmpty(entry.BrowserVersion) ? "(not installed)" : entry.BrowserVersion)} — {entry.Detail}");
                        Console.WriteLine($"                manifest: {entry.ManifestPath}");
                    }
                    return ExitOk;
                }
                case "identity":
                {
                    if (options.Json)
                    {
                        var identity = new
                        {
                            host = BrowserIntegrationInfo.HostName,
                            protocolVersion = BrowserActions.ProtocolVersion,
                            chromiumKey = BrowserIntegrationInfo.ChromiumExtensionKey,
                            chromiumId = BrowserIntegrationInfo.ChromiumExtensionId,
                            chromiumIdCalc = ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey),
                            firefoxId = BrowserIntegrationInfo.FirefoxExtensionId
                        };
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(identity,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        return ExitOk;
                    }

                    Console.WriteLine($"host            : {BrowserIntegrationInfo.HostName}");
                    Console.WriteLine($"protocolVersion : {BrowserActions.ProtocolVersion}");
                    Console.WriteLine($"chromiumKey     : {BrowserIntegrationInfo.ChromiumExtensionKey}");
                    Console.WriteLine($"chromiumId      : {BrowserIntegrationInfo.ChromiumExtensionId}");
                    Console.WriteLine($"chromiumIdCalc  : {ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey)}");
                    Console.WriteLine($"firefoxId       : {BrowserIntegrationInfo.FirefoxExtensionId}");
                    return ExitOk;
                }
                default:
                    return ExitUsage;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
    }

    /// <summary>
    /// Browsers launch the host with arguments of their own: Chromium appends
    /// <c>&lt;origin&gt; --parent-window=&lt;hwnd&gt;</c>, Firefox appends the manifest path and the
    /// extension id. Nothing in them is an instruction for this host, but treating them as unknown
    /// arguments would make the process exit before it ever reads the message.
    /// </summary>
    private static bool TryAcceptBrowserArgument(string argument, HostOptions options)
    {
        if (string.Equals(argument, "--parent-window", StringComparison.Ordinal) ||
            argument.StartsWith("--parent-window=", StringComparison.Ordinal))
            return true;

        if (argument.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
        {
            options.CallerOrigin ??= argument;
            return true;
        }

        if (argument.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(argument.TrimEnd('/'), BrowserIntegrationInfo.FirefoxExtensionId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        NovaDownload native messaging host.

          (no arguments)                Native messaging mode: framed JSON on stdin/stdout.
          --register                    Register the host for Chrome, Edge and Firefox (HKCU).
          --unregister                  Remove the registration.
          --status                      Print the current registration and resolution state.
          --identity                    Print host name, extension ids and the derived id check.

        Options:
          --host <path>                 Host executable to register (defaults to this process).
          --manifest-dir <path>         Where the manifests are written.
          --app <path>                  Explicit NovaDownload.App.exe path.
          --pipe <name>                 Override the local pipe name.
          --no-launch                   Never start the desktop application.
          --verbose                     Mirror diagnostics to stderr.
          --json                        Emit JSON instead of aligned text (--identity, --status).
        """);
}
