using System.IO.Pipes;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;

namespace NovaDownload.BrowserIntegration.Server;

/// <summary>
/// Pipe server hosted inside the desktop application. One request per connection, framed exactly
/// like a native message, answered with a single response. Nothing but a validated request can
/// leave this loop.
/// </summary>
public sealed class BrowserBridgeServer : IAsyncDisposable
{
    private static readonly TimeSpan PerConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly BrowserRequestProcessor _processor;
    private readonly Action<string>? _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string PipeName { get; }

    /// <summary>Connections answered since the last start — used by the settings page and tests.</summary>
    public int HandledRequests { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public BrowserBridgeServer(BrowserRequestProcessor processor, string? pipeName = null, Action<string>? log = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _log = log;
        PipeName = pipeName ?? BrowserBridgeProtocol.ResolvePipeName();
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        try { await _cts.CancelAsync(); } catch { }
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                try
                {
                    server = BrowserBridgeProtocol.CreateServer(PipeName);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Another NovaDownload instance already owns the pipe: keep the process alive,
                    // the other instance answers the browser.
                    _log?.Invoke($"bridge pipe unavailable ({ex.GetType().Name}: {ex.Message}); retrying.");
                    await Task.Delay(RestartDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PerConnectionTimeout);
                await HandleConnectionAsync(server, timeout.Token).ConfigureAwait(false);
                HandledRequests++;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                _log?.Invoke("bridge request timed out.");
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                _log?.Invoke($"bridge connection failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (server != null) await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One framed request in, one framed response out. Public so the pipe can be tested directly.</summary>
    public async Task<BrowserResponse> HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        string? payload;
        try
        {
            payload = await NativeMessageProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = BrowserResponse.Fail(ResponseCodes.MalformedMessage, ex.Message);
            await TryWriteAsync(stream, failure, cancellationToken).ConfigureAwait(false);
            return failure;
        }

        if (payload == null)
            return BrowserResponse.Fail(ResponseCodes.MalformedMessage, "Client closed the connection.");

        var response = await _processor.HandleJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        await TryWriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task TryWriteAsync(Stream stream, BrowserResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await NativeMessageProtocol
                .WriteMessageAsync(stream, System.Text.Json.JsonSerializer.Serialize(response, BrowserBridgeProtocol.Json), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The client gave up; nothing left to answer.
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
