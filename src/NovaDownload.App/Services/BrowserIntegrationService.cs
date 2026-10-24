using System.IO;
using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Registration;
using NovaDownload.BrowserIntegration.Server;
using NovaDownload.Core.Interfaces;

namespace NovaDownload.App.Services;

/// <summary>
/// Hosts the browser bridge inside the desktop application: owns the pipe server, the request
/// processor (validate → deduplicate → existing engine) and the per-user registration helpers.
/// </summary>
public sealed class BrowserIntegrationService : IAsyncDisposable
{
    private readonly IDownloadService _downloads;
    private readonly ISettingsService _settings;
    private readonly string _appVersion;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private BrowserBridgeServer? _server;
    private BrowserRequestProcessor? _processor;

    public BrowserIntegrationService(IDownloadService downloads, ISettingsService settings, Action<string>? log = null)
    {
        _downloads = downloads;
        _settings = settings;
        _log = log;
        _appVersion = typeof(BrowserIntegrationService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    /// <summary>Raised on the pipe thread when a download was queued or the app was asked to open.</summary>
    public event Action<BrowserResponse>? RequestAccepted;

    public bool IsRunning => _server is { IsRunning: true };

    public int HandledRequests => _server?.HandledRequests ?? 0;

    public string PipeName => _server?.PipeName ?? BrowserBridgeProtocol.ResolvePipeName();

    public void Start()
    {
        lock (_gate)
        {
            if (_server is { IsRunning: true }) return;

            _processor = new BrowserRequestProcessor(_downloads, _settings, _appVersion,
                onAccepted: response => RequestAccepted?.Invoke(response));
            _server = new BrowserBridgeServer(_processor, log: message => _log?.Invoke(message));
            _server.Start();
            _log?.Invoke($"browser bridge listening on pipe '{_server.PipeName}'");
        }
    }

    /// <summary>Applies a validated request that arrived through the URL protocol or the command line.</summary>
    public async Task<BrowserResponse> HandleStartupRequestAsync(StartupRequest request, CancellationToken cancellationToken = default)
    {
        var processor = _processor;
        if (processor == null)
            return BrowserResponse.Fail(ResponseCodes.InternalError, "Browser integration is not initialised.", BrowserActions.Download);

        return await processor.HandleAsync(request.ToBrowserRequest(), cancellationToken).ConfigureAwait(false);
    }

    public RegistrationReport InspectRegistration()
    {
        var host = NativeHostLocator.TryResolve(out _);
        var report = NativeHostRegistrar.Inspect(hostExecutablePath: host);
        var protocol = UrlProtocolRegistrar.IsRegistered();
        return report with
        {
            Entries = report.Entries.Select(entry => entry with
            {
                Detail = entry.Detail + (protocol ? " URL protocol registered." : " URL protocol not registered.")
            }).ToArray()
        };
    }

    /// <summary>Registers manifests, registry keys and the URL protocol for the current user.</summary>
    public RegistrationReport RegisterForCurrentUser(out string? error)
    {
        error = null;
        var host = NativeHostLocator.TryResolve(out var source);
        if (host == null)
        {
            error = "NovaDownload.NativeHost.exe was not found. Build or install the browser integration first.";
            return NativeHostRegistrar.Inspect();
        }

        _log?.Invoke($"registering native messaging host from {source}");
        var report = NativeHostRegistrar.Register(host);
        try
        {
            UrlProtocolRegistrar.Register(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppLocator.AppExecutableName));
        }
        catch (Exception ex)
        {
            error = $"URL protocol registration failed: {ex.Message}";
        }
        return report;
    }

    public async Task StopAsync()
    {
        BrowserBridgeServer? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }

        if (server != null) await server.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
