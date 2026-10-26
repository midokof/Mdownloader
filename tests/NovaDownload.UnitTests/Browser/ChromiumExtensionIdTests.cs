using NovaDownload.BrowserIntegration;
using NovaDownload.BrowserIntegration.Registration;

namespace NovaDownload.UnitTests.Browser;

public class ChromiumExtensionIdTests
{
    [Fact]
    public void The_Shipped_Key_Derives_The_Shipped_Id()
    {
        var derived = ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey);

        Assert.Equal(BrowserIntegrationInfo.ChromiumExtensionId, derived);
    }

    [Fact]
    public void Derived_Ids_Use_Only_The_Chromium_Alphabet()
    {
        var derived = ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey);

        Assert.Equal(32, derived.Length);
        Assert.All(derived, c => Assert.True(c is >= 'a' and <= 'p', $"unexpected character '{c}'"));
        Assert.True(ChromiumExtensionId.LooksValid(derived));
    }

    [Fact]
    public void Derivation_Is_Deterministic()
    {
        var first = ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey);
        var second = ChromiumExtensionId.FromPublicKey(BrowserIntegrationInfo.ChromiumExtensionKey);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_Different_Key_Derives_A_Different_Id()
    {
        // Same length, different content: the id must follow the bytes, not a cached constant.
        var bytes = Convert.FromBase64String(BrowserIntegrationInfo.ChromiumExtensionKey);
        bytes[^1] ^= 0x01;
        var altered = Convert.ToBase64String(bytes);

        Assert.NotEqual(BrowserIntegrationInfo.ChromiumExtensionId, ChromiumExtensionId.FromPublicKey(altered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Missing_Key_Is_An_Error(string? key)
    {
        Assert.Throws<ArgumentException>(() => ChromiumExtensionId.FromPublicKey(key!));
    }

    [Fact]
    public void A_Key_That_Is_Not_Base64_Is_An_Error()
    {
        Assert.Throws<ArgumentException>(() => ChromiumExtensionId.FromPublicKey("not base64 ###"));
    }

    [Theory]
    [InlineData("pnemgdlefleemkhignbgaclaelffmbah", true)]
    [InlineData("PNEMGDLEFLEEMKHIGNBGACLAELFFMBAH", false)]
    [InlineData("pnemgdlefleemkhignbgaclaelffmba", false)]
    [InlineData("znemgdlefleemkhignbgaclaelffmbah", false)]
    [InlineData(null, false)]
    public void Shape_Validation_Matches_Chromium_Rules(string? id, bool expected)
    {
        Assert.Equal(expected, ChromiumExtensionId.LooksValid(id));
    }
}
