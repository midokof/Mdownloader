using System.IO.Pipes;
using System.Text.Json;
using NovaDownload.BrowserIntegration.Models;

namespace NovaDownload.BrowserIntegration.Protocol;

/// <summary>
/// Local transport between the native messaging host and the running desktop application.
/// A per-user named pipe with a current-user-only DACL: no TCP port, no listening socket,
/// nothing reachable from another machine or another account.
/// </summary>
public static class BrowserBridgeProtocol
{
    public const string PipeNamePrefix = "NovaDownload.Browser.";

    /// <summary>Test/automation hook: forces a known pipe name instead of the per-user default.</summary>
    public const string PipeNameOverrideVariable = "NOVADOWNLOAD_PIPE_NAME";

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    /// <summary>
    /// Pipe name for the current user. The DACL is what actually protects the pipe; the name
    /// only has to be stable across the app and the host process.
    /// </summary>
    public static string ResolvePipeName()
    {
        var forced = Environment.GetEnvironmentVariable(PipeNameOverrideVariable);
        if (!string.IsNullOrWhiteSpace(forced)) return forced.Trim();

        var domain = Sanitize(Environment.UserDomainName);
        var user = Sanitize(Environment.UserName);
        var name = $"{PipeNamePrefix}{domain}.{user}";
        return name.Length <= 200 ? name : name[..200];
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "user";
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_')
            .ToArray();
        return new string(chars);
    }

    public static NamedPipeServerStream CreateServer(string pipeName) =>
        new(pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            ServerOptions());

    private static PipeOptions ServerOptions()
    {
        var options = PipeOptions.Asynchronous;
        // Restricts the DACL to the interactive user: another account on the same machine
        // cannot connect even if it guesses the name.
        if (OperatingSystem.IsWindows()) options |= PipeOptions.CurrentUserOnly;
        return options;
    }

    /// <summary>Connects to the running application, if any. Never throws.</summary>
    public static async Task<NamedPipeClientStream?> TryConnectAsync(string? pipeName = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        pipeName ??= ResolvePipeName();
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultConnectTimeout);
        try
        {
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>True when a NovaDownload instance is listening on the local pipe.</summary>
    public static async Task<bool> IsServerAvailableAsync(string? pipeName = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var client = await TryConnectAsync(pipeName, timeout, cancellationToken).ConfigureAwait(false);
        if (client == null) return false;
        await client.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends one request and returns the response. Never throws: an unreachable application
    /// becomes an <c>APP_UNAVAILABLE</c> response the extension can render.
    /// </summary>
    public static async Task<BrowserResponse> SendRequestAsync(BrowserRequest request, string? pipeName = null,
        TimeSpan? connectTimeout = null, CancellationToken cancellationToken = default)
    {
        var client = await TryConnectAsync(pipeName, connectTimeout, cancellationToken).ConfigureAwait(false);
        if (client == null)
            return BrowserResponse.Fail(ResponseCodes.AppUnavailable,
                "NovaDownload is not running and could not be started.", request.Action);

        await using (client)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await NativeMessageProtocol.WriteMessageAsync(client, JsonSerializer.Serialize(request, Json), cts.Token)
                    .ConfigureAwait(false);
                var payload = await NativeMessageProtocol.ReadMessageAsync(client, cts.Token).ConfigureAwait(false);
                if (payload == null)
                    return BrowserResponse.Fail(ResponseCodes.AppUnavailable,
                        "NovaDownload closed the connection before answering.", request.Action);

                var response = JsonSerializer.Deserialize<BrowserResponse>(payload, Json);
                return response ?? BrowserResponse.Fail(ResponseCodes.InternalError,
                    "NovaDownload returned an empty response.", request.Action);
            }
            catch (NativeMessageException ex)
            {
                return BrowserResponse.Fail(ResponseCodes.MalformedMessage, ex.Message, request.Action);
            }
            catch (OperationCanceledException)
            {
                return BrowserResponse.Fail(ResponseCodes.AppUnavailable, "NovaDownload did not answer in time.", request.Action);
            }
            catch (Exception ex)
            {
                return BrowserResponse.Fail(ResponseCodes.AppUnavailable, ex.Message, request.Action);
            }
        }
    }
}
