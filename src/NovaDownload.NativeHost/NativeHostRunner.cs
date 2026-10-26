using System.Text.Json;
using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Registration;
using NovaDownload.BrowserIntegration.Validation;

namespace NovaDownload.NativeHost;

/// <summary>
/// The native messaging host: a small, replaceable relay. It validates what the extension sent,
/// makes sure the desktop application is running, forwards the request over the local pipe and
/// returns the application's answer verbatim. It never touches the file system or the network.
/// </summary>
public sealed class NativeHostRunner
{
    private readonly HostOptions _options;
    private readonly HostLog _log;
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<CancellationToken, Task<bool>> _launch;

    public NativeHostRunner(HostOptions options, HostLog? log = null,
        Func<CancellationToken, Task<bool>>? probe = null,
        Func<CancellationToken, Task<bool>>? launch = null)
    {
        _options = options;
        _log = log ?? new HostLog(options.Verbose);
        _probe = probe ?? (ct => BrowserBridgeProtocol.IsServerAvailableAsync(_options.PipeName, TimeSpan.FromMilliseconds(400), ct));
        _launch = launch ?? (ct => AppLocator.TryStartAndWaitAsync(_options.LaunchTimeout, _options.PipeName, ct));
    }

    /// <summary>Native messaging loop: framed JSON in, framed JSON out, until the browser closes stdin.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        _log.Info($"host started (pipe={_options.PipeName ?? BrowserBridgeProtocol.ResolvePipeName()}, launch={_options.AllowLaunch})");
        if (!string.IsNullOrWhiteSpace(_options.CallerOrigin))
            _log.Info($"caller origin: {_options.CallerOrigin}");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? payload;
            try
            {
                payload = await NativeMessageProtocol.ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (NativeMessageException ex)
            {
                _log.Error($"malformed message: {ex.Message}");
                await WriteAsync(output, BrowserResponse.Fail(ResponseCodes.MalformedMessage, ex.Message), cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"stdin closed: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (payload == null) break;

            var response = await HandleAsync(payload, cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"cannot answer the browser: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        _log.Info("host exiting");
    }

    private static Task WriteAsync(Stream output, BrowserResponse response, CancellationToken cancellationToken) =>
        NativeMessageProtocol.WriteMessageAsync(output, JsonSerializer.Serialize(response, BrowserBridgeProtocol.Json), cancellationToken);

    /// <summary>Validates one payload and relays it. Never throws.</summary>
    public async Task<BrowserResponse> HandleAsync(string? payload, CancellationToken cancellationToken = default)
    {
        BrowserRequest? request;
        try
        {
            if (string.IsNullOrWhiteSpace(payload))
                return BrowserResponse.Fail(ResponseCodes.MalformedMessage, "Empty message.");
            request = JsonSerializer.Deserialize<BrowserRequest>(payload, BrowserBridgeProtocol.Json);
        }
        catch (JsonException)
        {
            _log.Error("payload rejected: not valid JSON");
            return BrowserResponse.Fail(ResponseCodes.MalformedMessage, "Message is not valid JSON.");
        }

        var validation = DownloadRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            _log.Error($"payload rejected: {validation.Code} — {validation.Message}");
            return BrowserResponse.Fail(validation.Code, validation.Message, request?.Action);
        }

        try
        {
            return await RelayAsync(request!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return BrowserResponse.Fail(ResponseCodes.AppUnavailable, "The request was cancelled.", request!.Action);
        }
        catch (Exception ex)
        {
            _log.Error($"relay failed: {ex.GetType().Name}: {ex.Message}");
            return BrowserResponse.Fail(ResponseCodes.InternalError, ex.Message, request!.Action);
        }
    }

    private async Task<BrowserResponse> RelayAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        var available = await _probe(cancellationToken).ConfigureAwait(false);

        if (!available && _options.AllowLaunch)
        {
            _log.Info("desktop application not reachable — starting it");
            available = await _launch(cancellationToken).ConfigureAwait(false);
        }

        if (!available)
        {
            var resolved = AppLocator.TryResolve(out var source);
            _log.Error($"desktop application unavailable ({source})");
            return BrowserResponse.Fail(ResponseCodes.AppUnavailable, resolved == null
                ? "NovaDownload is not installed on this computer. Install NovaDownload and try again."
                : "NovaDownload is not running and could not be started automatically. Start NovaDownload and try again.",
                request.Action);
        }

        var response = await BrowserBridgeProtocol
            .SendRequestAsync(request, _options.PipeName, TimeSpan.FromSeconds(3), cancellationToken)
            .ConfigureAwait(false);

        if (response.Code == ResponseCodes.AppUnavailable && _options.AllowLaunch)
        {
            // The application exited between the probe and the hand-off: one more attempt.
            _log.Error("desktop application dropped the connection — restarting it");
            if (await _launch(cancellationToken).ConfigureAwait(false))
            {
                response = await BrowserBridgeProtocol
                    .SendRequestAsync(request, _options.PipeName, TimeSpan.FromSeconds(3), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _log.Info($"relayed action={request.Action} code={response.Code} ok={response.Ok}");
        return response;
    }
}
