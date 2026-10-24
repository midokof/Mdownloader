using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Validation;

namespace NovaDownload.UnitTests.Browser;

public class DownloadRequestValidatorTests
{
    private static BrowserRequest Download(string? url = "https://cdn.example.com/clip.mp4") => new()
    {
        Action = BrowserActions.Download,
        Url = url,
        FileName = "clip.mp4"
    };

    [Fact]
    public void Null_Request_Is_Refused()
    {
        var result = DownloadRequestValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidRequest, result.Code);
        Assert.Null(result.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_Action_Is_Refused(string? action)
    {
        var result = DownloadRequestValidator.Validate(new BrowserRequest { Action = action });

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public void Unknown_Action_Is_Refused()
    {
        var result = DownloadRequestValidator.Validate(new BrowserRequest { Action = "rm -rf" });

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.UnknownAction, result.Code);
    }

    [Theory]
    [InlineData("blob:https://example.com/9d1f-4f")]
    [InlineData("data:video/mp4;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("ftp://example.com/a.mp4")]
    [InlineData("chrome://settings")]
    [InlineData("about:blank")]
    public void Only_Http_And_Https_Reach_The_Engine(string url)
    {
        var result = DownloadRequestValidator.Validate(Download(url));

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.UnsupportedScheme, result.Code);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path.mp4")]
    [InlineData("https://user:secret@example.com/a.mp4")]
    [InlineData("")]
    public void Malformed_Urls_Are_Refused(string url)
    {
        var result = DownloadRequestValidator.Validate(Download(url));

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public void Control_Characters_In_A_Url_Are_Refused()
    {
        var result = DownloadRequestValidator.Validate(Download("https://example.com/a.mp4\nHost: evil"));

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public void Oversized_Url_Is_Refused()
    {
        var url = "https://example.com/" + new string('a', DownloadRequestValidator.MaxUrlLength);
        var result = DownloadRequestValidator.Validate(Download(url));

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public void Fragments_Are_Stripped_From_The_Accepted_Url()
    {
        var result = DownloadRequestValidator.Validate(Download("https://example.com/video.mp4#t=12"));

        Assert.True(result.IsValid);
        Assert.Equal("https://example.com/video.mp4", result.Request!.Url);
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("../../evil.sh")]
    [InlineData("C:\\Windows\\evil.dll")]
    public void File_Name_Traversal_Is_Refused(string fileName)
    {
        var request = Download();
        request.FileName = fileName;

        var result = DownloadRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.UnsafeFileName, result.Code);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.mp4")]
    [InlineData("NUL.txt")]
    public void Reserved_File_Names_Are_Refused(string fileName)
    {
        var request = Download();
        request.FileName = fileName;

        var result = DownloadRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.UnsafeFileName, result.Code);
    }

    [Fact]
    public void File_Name_Hint_Is_Sanitized_Not_Trusted()
    {
        var request = Download();
        request.FileName = "holiday|clip.mp4";

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal("holiday_clip.mp4", result.Request!.FileName);
    }

    [Fact]
    public void Missing_File_Name_Is_Allowed_So_The_Server_Can_Name_The_File()
    {
        var request = Download();
        request.FileName = null;

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Null(result.Request!.FileName);
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"//server/share")]
    [InlineData(@"Relative\Folder")]
    [InlineData(@"C:\Downloads\..\Windows")]
    [InlineData(@"Q:\NoSuchDrive\Downloads")]
    public void Unsafe_Destinations_Are_Refused(string directory)
    {
        var request = Download();
        request.Directory = directory;

        var result = DownloadRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(ResponseCodes.InvalidDirectory, result.Code);
    }

    [Fact]
    public void Absolute_Local_Destination_Is_Normalized()
    {
        var request = Download();
        request.Directory = Path.Combine(Path.GetTempPath(), "nova-browser") + "\\";

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(request.Directory).TrimEnd('\\'), result.Request!.Directory);
    }

    [Theory]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("Audio/Mpeg", "audio/mpeg")]
    [InlineData("not a mime type", null)]
    [InlineData("video/mp4; codecs=avc1", null)]
    public void Media_Type_Is_Advisory_And_Filtered(string hint, string? expected)
    {
        var request = Download();
        request.MediaType = hint;

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Request!.MediaType);
    }

    [Fact]
    public void Browser_Token_Is_Filtered_But_Never_Fatal()
    {
        var request = Download();
        request.Browser = "edge";
        Assert.Equal("edge", DownloadRequestValidator.Validate(request).Request!.Browser);

        request.Browser = "../../etc";
        Assert.Null(DownloadRequestValidator.Validate(request).Request!.Browser);
    }

    [Fact]
    public void Request_Id_Must_Be_Safe()
    {
        var request = Download();
        request.RequestId = "8d1f4c1e-0000-4000-8000-000000000001";
        Assert.True(DownloadRequestValidator.Validate(request).IsValid);

        request.RequestId = "id with spaces";
        Assert.False(DownloadRequestValidator.Validate(request).IsValid);

        request.RequestId = new string('x', DownloadRequestValidator.MaxRequestIdLength + 1);
        Assert.False(DownloadRequestValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Non_Download_Actions_Never_Carry_A_Url()
    {
        var request = new BrowserRequest { Action = BrowserActions.Ping, Url = "file:///C:/secret" };

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Null(result.Request!.Url);
    }

    [Fact]
    public void Action_Is_Case_Insensitive()
    {
        var request = Download();
        request.Action = "  DOWNLOAD ";

        var result = DownloadRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(BrowserActions.Download, result.Request!.Action);
    }
}
