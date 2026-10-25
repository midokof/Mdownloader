using NovaDownload.BrowserIntegration.Models;
using NovaDownload.BrowserIntegration.Protocol;
using NovaDownload.BrowserIntegration.Validation;
using NovaDownload.Core.Common;
using NovaDownload.DownloadEngine.Torrenting;

namespace NovaDownload.UnitTests;

/// <summary>
/// The magnet: protocol handler, the browser bridge gate and the download router must agree on
/// what a torrent source is. These tests pin the rules and the agreement between the layers.
/// </summary>
public class TorrentSourceTests
{
    private const string ValidHash = "10553FF05A371CFDB0F25F9F0BD45AA937B442E8";

    [Theory]
    [InlineData("magnet:?xt=urn:btih:" + ValidHash)]
    [InlineData("MAGNET:?xt=urn:btih:" + ValidHash)]
    [InlineData("magnet:?dn=Sample&xt=urn:btih:" + ValidHash + "&tr=udp%3A%2F%2Ftracker.example%3A80")]
    [InlineData("magnet:?xt=urn:btih:10553ff05a371cfdb0f25f9f0bd45aa937b442e8")]
    // Base32 form (BEP 9): 32 base32 characters.
    [InlineData("magnet:?xt=urn:btih:BBTO6P2LSXG4X5Z5XAXZ3DVCU4J3DKQ4")]
    public void A_Magnet_With_An_Info_Hash_Is_A_Torrent_Source(string source)
    {
        Assert.True(TorrentSourceRules.TryNormalize(source, out var normalized, out var reason), reason);
        Assert.Equal(source, normalized);
        Assert.True(TorrentSource.IsTorrent(source));
    }

    [Theory]
    [InlineData("magnet:?dn=No+Hash")]
    [InlineData("magnet:?xt=urn:sha1:10553FF05A371CFDB0F25F9F0BD45AA937B442E8")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:")]
    public void A_Magnet_Without_A_Usable_Info_Hash_Is_Refused(string source)
    {
        Assert.False(TorrentSourceRules.TryNormalize(source, out _, out var reason));
        Assert.Contains("info hash", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(TorrentSource.IsTorrent(source));
    }

    [Fact]
    public void A_Remote_Torrent_File_Keeps_Its_Url_And_Loses_Its_Fragment()
    {
        const string source = "https://example.com/files/album.torrent#ignored";

        Assert.True(TorrentSourceRules.TryNormalize(source, out var normalized, out var reason), reason);
        Assert.Equal("https://example.com/files/album.torrent", normalized);
        Assert.True(TorrentSourceRules.IsRemoteTorrentFile(source));
    }

    [Theory]
    [InlineData("https://example.com/files/album.zip")]
    [InlineData("ftp://example.com/files/album.torrent")]
    [InlineData("//example.com/files/album.torrent")]
    public void ANon_Torrent_Link_Is_Refused(string source) =>
        Assert.False(TorrentSourceRules.IsTorrent(source));

    [Fact]
    public void A_Local_Torrent_File_Is_Normalized_To_An_Absolute_Path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nova-{Guid.NewGuid():n}.torrent");
        File.WriteAllBytes(path, [0]);
        try
        {
            Assert.True(TorrentSourceRules.TryNormalize(path, out var normalized, out var reason), reason);
            Assert.Equal(Path.GetFullPath(path), normalized);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Missing_Local_Torrent_File_Is_Refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():n}.torrent");
        Assert.False(TorrentSourceRules.TryNormalize(path, out _, out var reason));
        Assert.Contains("does not exist", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Source_Longer_Than_The_Cap_Or_Carrying_Control_Characters_Is_Refused()
    {
        var longMagnet = "magnet:?xt=urn:btih:" + ValidHash + "&tr=" + new string('a', TorrentSourceRules.MaxSourceLength);
        Assert.False(TorrentSourceRules.TryNormalize(longMagnet, out _, out var lengthReason));
        Assert.Contains("longer than", lengthReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(TorrentSourceRules.TryNormalize($"magnet:?xt=urn:btih:{ValidHash}\u0001", out _, out var controlReason));
        Assert.Contains("control characters", controlReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bridge gate: a magnet is a valid request once it is tagged as a torrent, and still
    /// refused as a media download, where only http(s) is fetchable.
    /// </summary>
    [Fact]
    public void The_Gate_Accepts_A_Magnet_Only_Under_The_Torrent_Action()
    {
        const string magnet = "magnet:?xt=urn:btih:" + ValidHash;

        var torrent = DownloadRequestValidator.Validate(new BrowserRequest
        {
            Action = BrowserActions.Torrent,
            Url = magnet
        });
        Assert.True(torrent.IsValid, torrent.Message);
        Assert.Equal(magnet, torrent.Request!.Url);

        var media = DownloadRequestValidator.Validate(new BrowserRequest
        {
            Action = BrowserActions.Download,
            Url = magnet
        });
        Assert.False(media.IsValid);
        Assert.Equal(ResponseCodes.UnsupportedScheme, media.Code);
    }

    /// <summary>Every source the router accepts must be accepted by the gate, and the reverse.</summary>
    [Theory]
    [InlineData("magnet:?xt=urn:btih:" + ValidHash)]
    [InlineData("magnet:?dn=No+Hash")]
    [InlineData("https://example.com/a.torrent")]
    [InlineData("https://example.com/a.mp4")]
    [InlineData("magnet:?xt=urn:sha1:10553FF05A371CFDB0F25F9F0BD45AA937B442E8")]
    public void The_Router_And_The_Gate_Agree(string source)
    {
        var gate = DownloadRequestValidator.Validate(new BrowserRequest
        {
            Action = BrowserActions.Torrent,
            Url = source
        });

        Assert.Equal(TorrentSource.IsTorrent(source), gate.IsValid);
    }

    [Fact]
    public void DisplayName_Falls_Back_To_The_Info_Hash_When_No_Name_Is_Announced()
    {
        Assert.Equal("Album Cut",
            TorrentSource.DisplayName("magnet:?xt=urn:btih:" + ValidHash + "&dn=Album%20Cut"));

        Assert.Equal(ValidHash,
            TorrentSource.DisplayName("magnet:?xt=urn:btih:" + ValidHash));
    }
}
