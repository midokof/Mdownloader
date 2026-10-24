using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.UnitTests;

public class FormatterTests
{
    public FormatterTests() => Localizer.Apply("en");

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void Bytes_Uses_Binary_Units_With_Invariant_Decimal(long input, string expected) =>
        Assert.Equal(expected, Formatters.Bytes(input));

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(2048, "2 KB/s")]
    public void Speed_Appends_Per_Second(double input, string expected) =>
        Assert.Equal(expected, Formatters.Speed(input));

    [Theory]
    [InlineData(0, 100, "—")]
    [InlineData(500, 0, "—")]
    [InlineData(1000, 1000, "1s")]
    [InlineData(6000, 1000, "6s")]
    [InlineData(90_000, 1000, "1m 30s")]
    [InlineData(7_200_000, 1000, "2h 0m")]
    [InlineData(259_200_000, 1000, "3d 0h")]
    public void Eta_Formats_Remaining_Time(long remaining, double speed, string expected) =>
        Assert.Equal(expected, Formatters.Eta(remaining, speed));
}

public class DownloadItemTests
{
    public DownloadItemTests() => Localizer.Apply("en");

    [Fact]
    public void ProgressPercent_Is_Calculated_From_Bytes()
    {
        var item = new DownloadItem { FileSize = 1000, ProgressBytes = 250 };
        Assert.Equal(25, item.ProgressPercent, 3);
    }

    [Fact]
    public void ProgressPercent_Clamps_And_Handles_Unknown_Size()
    {
        var item = new DownloadItem { FileSize = 100, ProgressBytes = 500 };
        Assert.Equal(100, item.ProgressPercent, 3);

        var unknown = new DownloadItem { FileSize = 0, ProgressBytes = 500 };
        Assert.Equal(0, unknown.ProgressPercent, 3);

        unknown.Status = DownloadStatus.Completed;
        Assert.Equal(100, unknown.ProgressPercent, 3);
    }

    [Fact]
    public void Extension_And_Folder_Are_Derived_From_Path()
    {
        var item = new DownloadItem
        {
            FileName = "archive.tar.gz",
            DestinationPath = @"C:\Downloads\archive.tar.gz"
        };

        Assert.Equal("GZ", item.Extension);
        Assert.Equal(@"C:\Downloads", item.Folder);
        Assert.Equal("A", item.Initial);
    }

    [Fact]
    public void Metric_Text_Tracks_Status()
    {
        var item = new DownloadItem
        {
            FileSize = 10_000_000,
            ProgressBytes = 2_500_000,
            Status = DownloadStatus.Downloading,
            SpeedBps = 1_000_000
        };
        Assert.Equal("976.6 KB/s", item.MetricPrimary);
        Assert.StartsWith("ETA ", item.MetricSecondary);

        item.Status = DownloadStatus.Paused;
        Assert.StartsWith("Paused ", item.MetricPrimary);

        item.Status = DownloadStatus.Completed;
        Assert.StartsWith("Finished ", item.MetricPrimary);
        Assert.Equal("9.5 MB", item.MetricSecondary);
    }

    [Fact]
    public void Resume_Is_Only_Offered_For_Interrupted_States()
    {
        Assert.True(new DownloadItem { Status = DownloadStatus.Paused }.CanResume);
        Assert.True(new DownloadItem { Status = DownloadStatus.Failed }.CanResume);
        Assert.False(new DownloadItem { Status = DownloadStatus.Downloading }.CanResume);
        Assert.False(new DownloadItem { Status = DownloadStatus.Completed }.CanResume);
        Assert.False(new DownloadItem { Status = DownloadStatus.Completed }.IsOngoing);
    }
}
