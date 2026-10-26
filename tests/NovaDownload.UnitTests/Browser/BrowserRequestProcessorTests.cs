using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Deduplication;
using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Models;

namespace NovaDownload.UnitTests.Browser;

public class BrowserRequestProcessorTests
{
    private static BrowserRequestProcessor Build(
        out RecordingDownloadService engine,
        AppSettings? settings = null,
        Action<BrowserResponse>? onAccepted = null,
        RequestDeduplicator? deduplicator = null)
    {
        engine = new RecordingDownloadService();
        return new BrowserRequestProcessor(engine, new FakeSettingsService(settings), "1.0.0", deduplicator, onAccepted);
    }

    private static BrowserRequest Download(string url = "https://cdn.example.com/clip.mp4", string? requestId = null) => new()
    {
        Action = BrowserActions.Download,
        Url = url,
        FileName = "clip.mp4",
        RequestId = requestId,
        Browser = "edge"
    };

    [Fact]
    public async Task A_Ping_Reports_State_Without_Starting_Anything()
    {
        var processor = Build(out var engine);

        var response = await processor.HandleAsync(new BrowserRequest { Action = BrowserActions.Ping });

        Assert.True(response.Ok);
        Assert.Equal(ResponseCodes.Ok, response.Code);
        Assert.Equal(BrowserActions.ProtocolVersion, response.ProtocolVersion);
        Assert.Equal("novadownload-app", response.Server);
        Assert.Equal("1.0.0", response.AppVersion);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task A_Download_Reaches_The_Engine_And_Is_Reported_Back()
    {
        var accepted = 0;
        var processor = Build(out var engine, onAccepted: _ => accepted++);

        var response = await processor.HandleAsync(Download(requestId: "req-1"));

        Assert.True(response.Ok);
        Assert.Equal(ResponseCodes.Ok, response.Code);
        Assert.False(response.Duplicate);
        Assert.NotNull(response.DownloadId);
        Assert.Equal("clip.mp4", response.FileName);
        Assert.Equal(1, accepted);
        Assert.Single(engine.Started);
        Assert.Equal("https://cdn.example.com/clip.mp4", engine.Started[0].Url);
        Assert.Equal("clip.mp4", engine.Started[0].FileName);
    }

    [Fact]
    public async Task The_Configured_Destination_Is_Used_When_The_Browser_Sends_None()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nova-browser-destination");
        var settings = new AppSettings { DefaultDownloadDirectory = directory };
        var processor = Build(out var engine, settings);

        await processor.HandleAsync(Download());

        Assert.Equal(directory, engine.Started[0].Directory);
    }

    [Fact]
    public async Task A_Requested_Destination_Wins_Over_The_Default()
    {
        var requested = Path.Combine(Path.GetTempPath(), "nova-requested");
        var settings = new AppSettings { DefaultDownloadDirectory = Path.Combine(Path.GetTempPath(), "nova-default") };
        var processor = Build(out var engine, settings);

        var request = Download();
        request.Directory = requested;

        await processor.HandleAsync(request);

        Assert.Equal(Path.GetFullPath(requested).TrimEnd('\\'), engine.Started[0].Directory);
    }

    [Fact]
    public async Task An_Invalid_Configured_Destination_Falls_Back_To_Downloads()
    {
        var settings = new AppSettings { DefaultDownloadDirectory = @"\\server\share" };
        var processor = Build(out var engine, settings);

        await processor.HandleAsync(Download());

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Assert.Equal(expected, engine.Started[0].Directory);
    }

    [Fact]
    public async Task Turning_Integration_Off_Stops_Downloads_At_The_Door()
    {
        var settings = new AppSettings { EnableBrowserIntegration = false };
        var processor = Build(out var engine, settings);

        var response = await processor.HandleAsync(Download());

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.Disabled, response.Code);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task Pings_Still_Answer_While_Integration_Is_Off()
    {
        var settings = new AppSettings { EnableBrowserIntegration = false };
        var processor = Build(out _, settings);

        var response = await processor.HandleAsync(new BrowserRequest { Action = BrowserActions.Ping });

        Assert.True(response.Ok);
        Assert.False(response.IntegrationEnabled);
    }

    [Fact]
    public async Task A_Transfer_Already_In_The_Queue_Is_Not_Started_Twice()
    {
        var processor = Build(out var engine);
        engine.Seed(new DownloadItem
        {
            Url = "https://cdn.example.com/clip.mp4",
            FileName = "clip.mp4",
            Status = DownloadStatus.Downloading
        });

        var response = await processor.HandleAsync(Download(requestId: "req-2"));

        Assert.True(response.Ok);
        Assert.Equal(ResponseCodes.Duplicate, response.Code);
        Assert.True(response.Duplicate);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task A_Replayed_Request_Id_Returns_The_Original_Transfer()
    {
        var deduplicator = new RequestDeduplicator();
        var processor = Build(out var engine, deduplicator: deduplicator);

        var first = await processor.HandleAsync(Download(requestId: "req-3"));
        var second = await processor.HandleAsync(Download(requestId: "req-3"));

        Assert.True(first.Ok);
        Assert.Equal(ResponseCodes.Duplicate, second.Code);
        Assert.True(second.Duplicate);
        Assert.Equal(first.DownloadId, second.DownloadId);
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task A_Finished_Transfer_Does_Not_Block_A_New_Request()
    {
        var processor = Build(out var engine);
        engine.Seed(new DownloadItem
        {
            Url = "https://cdn.example.com/clip.mp4",
            FileName = "clip.mp4",
            Status = DownloadStatus.Completed
        });

        var response = await processor.HandleAsync(Download(requestId: "req-4"));

        Assert.Equal(ResponseCodes.Ok, response.Code);
        Assert.Single(engine.Started);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task Malformed_Payloads_Are_Reported_Not_Thrown(string? payload)
    {
        var processor = Build(out var engine);

        var response = await processor.HandleJsonAsync(payload);

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.MalformedMessage, response.Code);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task A_Json_Request_Is_Accepted()
    {
        var processor = Build(out var engine);
        const string payload = """
            {"action":"download","url":"https://cdn.example.com/clip.mp4","fileName":"clip.mp4","requestId":"req-5","browser":"chrome"}
            """;

        var response = await processor.HandleJsonAsync(payload);

        Assert.True(response.Ok);
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task An_Unsupported_Scheme_Is_Reported_With_Its_Own_Code()
    {
        var processor = Build(out var engine);

        var response = await processor.HandleAsync(Download("blob:https://example.com/abc"));

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.UnsupportedScheme, response.Code);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task An_Unknown_Action_Is_Reported()
    {
        var processor = Build(out _);

        var response = await processor.HandleAsync(new BrowserRequest { Action = "delete-everything" });

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.UnknownAction, response.Code);
    }

    [Fact]
    public async Task An_Engine_Failure_Becomes_A_Clean_Error_Response()
    {
        var processor = Build(out var engine);
        engine.StartFailure = new InvalidOperationException("disk is full");

        var response = await processor.HandleAsync(Download());

        Assert.False(response.Ok);
        Assert.Equal(ResponseCodes.InternalError, response.Code);
        Assert.Contains("disk is full", response.Message);
    }

    [Fact]
    public async Task An_Open_Request_Never_Touches_The_Engine()
    {
        var accepted = 0;
        var processor = Build(out var engine, onAccepted: _ => accepted++);

        var response = await processor.HandleAsync(new BrowserRequest { Action = BrowserActions.Open });

        Assert.True(response.Ok);
        Assert.Equal(1, accepted);
        Assert.Empty(engine.Started);
    }
}
