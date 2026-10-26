using System.Text.Json;
using NovaDownload.BrowserIntegration.Deduplication;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Validation;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Interfaces;

namespace NovaDownload.BrowserIntegration;

/// <summary>
/// Turns a validated browser request into work on the existing download engine. This is the
/// whole browser-side pipeline: validate, deduplicate, hand to <see cref="IDownloadService"/>.
/// No second engine, no parallel data model.
/// </summary>
public sealed class BrowserRequestProcessor
{
    private readonly IDownloadService _downloads;
    private readonly ISettingsService _settings;
    private readonly RequestDeduplicator _deduplicator;
    private readonly Action<BrowserResponse>? _onAccepted;
    private readonly string _appVersion;

    public BrowserRequestProcessor(
        IDownloadService downloads,
        ISettingsService settings,
        string? appVersion = null,
        RequestDeduplicator? deduplicator = null,
        Action<BrowserResponse>? onAccepted = null)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _appVersion = appVersion ?? "1.0.0";
        _deduplicator = deduplicator ?? new RequestDeduplicator();
        _onAccepted = onAccepted;
    }

    /// <summary>Parses then handles a raw payload. Never throws.</summary>
    public async Task<BrowserResponse> HandleJsonAsync(string? payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return BrowserResponse.Fail(ResponseCodes.MalformedMessage, "Empty payload.");
        if (payload.Length > NativeMessageProtocol.MaxMessageBytes / 4)
            return BrowserResponse.Fail(ResponseCodes.PayloadTooLarge, "Payload is too large.");

        BrowserRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BrowserRequest>(payload, BrowserBridgeProtocol.Json);
        }
        catch (JsonException)
        {
            return BrowserResponse.Fail(ResponseCodes.MalformedMessage, "Payload is not valid JSON.");
        }

        return await HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates and applies one request. Never throws.</summary>
    public async Task<BrowserResponse> HandleAsync(BrowserRequest? request, CancellationToken cancellationToken = default)
    {
        var validation = DownloadRequestValidator.Validate(request);
        if (!validation.IsValid)
            return Decorated(BrowserResponse.Fail(validation.Code, validation.Message, request?.Action));

        var action = validation.Request!.Action;
        try
        {
            return action switch
            {
                BrowserActions.Ping => Decorated(Snapshot(action)),
                BrowserActions.Status => Decorated(Snapshot(action)),
                BrowserActions.Open => HandleOpen(action),
                BrowserActions.Download => await HandleDownloadAsync(validation.Request, cancellationToken).ConfigureAwait(false),
                BrowserActions.Torrent => await HandleDownloadAsync(validation.Request, cancellationToken).ConfigureAwait(false),
                _ => Decorated(BrowserResponse.Fail(ResponseCodes.UnknownAction, $"Unsupported action '{action}'.", action))
            };
        }
        catch (Exception ex)
        {
            return Decorated(BrowserResponse.Fail(ResponseCodes.InternalError, Trim(ex.Message), action));
        }
    }

    private BrowserResponse HandleOpen(string action)
    {
        var response = Snapshot(action);
        response.Message = "NovaDownload is running.";
        _onAccepted?.Invoke(response);
        return response;
    }

    private async Task<BrowserResponse> HandleDownloadAsync(ValidatedRequest request, CancellationToken cancellationToken)
    {
        var action = request.Action;

        // The magnet: handler and the command line are not "browser integration": switching the
        // extension off must not stop a torrent link from opening the application.
        if (action != BrowserActions.Torrent && !_settings.Current.EnableBrowserIntegration)
            return Decorated(BrowserResponse.Fail(ResponseCodes.Disabled,
                "Browser integration is turned off in NovaDownload settings.", action));

        var url = request.Url!;

        // A transfer for the same URL that is still alive in the engine is the same download.
        var inFlight = _downloads.Snapshot().FirstOrDefault(item =>
            !item.IsFinished && string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        if (inFlight != null)
        {
            var duplicate = Decorated(new BrowserResponse
            {
                Ok = true,
                Code = ResponseCodes.Duplicate,
                Action = action,
                Duplicate = true,
                DownloadId = inFlight.Id,
                FileName = inFlight.FileName,
                DestinationPath = inFlight.DestinationPath,
                Message = "This transfer is already in the NovaDownload queue."
            });
            return duplicate;
        }

        var key = RequestDeduplicator.BuildKey(request.RequestId, url);
        if (!_deduplicator.TryBegin(key, out var knownId))
        {
            // Same message replayed: wait briefly so the first request can publish its id.
            var id = knownId;
            for (var attempt = 0; attempt < 10 && id is null; attempt++)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                id = _deduplicator.GetDownloadId(key);
            }

            return Decorated(new BrowserResponse
            {
                Ok = true,
                Code = ResponseCodes.Duplicate,
                Action = action,
                Duplicate = true,
                DownloadId = id,
                Message = "This request was already sent to NovaDownload."
            });
        }

        var directory = ResolveDirectory(request.Directory);
        var item = await _downloads.StartDownloadAsync(url, directory, request.FileName, cancellationToken)
            .ConfigureAwait(false);

        _deduplicator.Attach(key, item.Id);

        var response = Decorated(new BrowserResponse
        {
            Ok = true,
            Code = ResponseCodes.Ok,
            Action = action,
            DownloadId = item.Id,
            FileName = item.FileName,
            DestinationPath = item.DestinationPath,
            Message = $"'{item.FileName}' was added to NovaDownload."
        });

        _onAccepted?.Invoke(response);
        return response;
    }

    /// <summary>Requested folder, else the configured default, else the user's Downloads folder.</summary>
    private string ResolveDirectory(string? requested)
    {
        if (!string.IsNullOrEmpty(requested)) return requested;

        var configured = DownloadRequestValidator.ValidateDirectory(_settings.Current.DefaultDownloadDirectory, out _);
        if (!string.IsNullOrEmpty(configured)) return configured;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private BrowserResponse Snapshot(string action)
    {
        var items = _downloads.Snapshot();
        return Decorated(new BrowserResponse
        {
            Ok = true,
            Code = ResponseCodes.Ok,
            Action = action,
            ActiveDownloads = items.Count(item => item.IsOngoing),
            FinishedDownloads = items.Count(item => item.IsFinished),
            Message = "NovaDownload is running."
        });
    }

    private BrowserResponse Decorated(BrowserResponse response)
    {
        response.ProtocolVersion = BrowserActions.ProtocolVersion;
        response.AppVersion = _appVersion;
        response.Server = "novadownload-app";
        response.IntegrationEnabled = _settings.Current.EnableBrowserIntegration;
        response.DefaultDirectory ??= _settings.Current.DefaultDownloadDirectory;
        return response;
    }

    private static string Trim(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "Unexpected failure." :
        message.Length <= 400 ? message : message[..400];
}
