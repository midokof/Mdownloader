using System.Text;
using NovaDownload.BrowserIntegration.Protocol;

namespace NovaDownload.UnitTests.Browser;

public class NativeMessageProtocolTests
{
    /// <summary>A stream that hands out at most one byte per read, to exercise the fill loops.</summary>
    private sealed class DripStream : MemoryStream
    {
        public DripStream(byte[] buffer) : base(buffer) { }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) =>
            base.ReadAsync(destination[..Math.Min(1, destination.Length)], cancellationToken);
    }

    [Fact]
    public async Task Round_Trip_Preserves_The_Payload()
    {
        const string payload = "{\"action\":\"download\",\"url\":\"https://exemple.fr/vidéo.mp4\"}";
        var stream = new MemoryStream();

        await NativeMessageProtocol.WriteMessageAsync(stream, payload);
        stream.Position = 0;

        Assert.Equal(payload, await NativeMessageProtocol.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task The_Frame_Is_A_Little_Endian_Length_Prefix()
    {
        var stream = new MemoryStream();
        await NativeMessageProtocol.WriteMessageAsync(stream, "abc");

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, bytes[..4]);
        Assert.Equal("abc", Encoding.UTF8.GetString(bytes[4..]));
    }

    [Fact]
    public async Task Framed_Reads_Survive_One_Byte_At_A_Time()
    {
        var stream = new MemoryStream();
        await NativeMessageProtocol.WriteMessageAsync(stream, "{\"action\":\"ping\"}");
        var drip = new DripStream(stream.ToArray());

        Assert.Equal("{\"action\":\"ping\"}", await NativeMessageProtocol.ReadMessageAsync(drip));
    }

    [Fact]
    public async Task A_Clean_End_Of_Stream_Returns_Null()
    {
        Assert.Null(await NativeMessageProtocol.ReadMessageAsync(new MemoryStream()));
    }

    [Fact]
    public async Task A_Truncated_Header_Throws()
    {
        var stream = new MemoryStream(new byte[] { 5, 0 });

        await Assert.ThrowsAsync<NativeMessageException>(() => NativeMessageProtocol.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task A_Truncated_Body_Throws()
    {
        var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, (byte)'{', (byte)'}' });

        await Assert.ThrowsAsync<NativeMessageException>(() => NativeMessageProtocol.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Invalid_Lengths_Are_Refused()
    {
        await Assert.ThrowsAsync<NativeMessageException>(() =>
            NativeMessageProtocol.ReadMessageAsync(new MemoryStream(new byte[] { 0, 0, 0, 0 })));

        var negative = BitConverter.GetBytes(-5);
        await Assert.ThrowsAsync<NativeMessageException>(() =>
            NativeMessageProtocol.ReadMessageAsync(new MemoryStream(negative)));
    }

    [Fact]
    public async Task Oversized_Messages_Are_Refused_Before_Allocation()
    {
        var header = BitConverter.GetBytes(NativeMessageProtocol.MaxMessageBytes + 1);

        var error = await Assert.ThrowsAsync<NativeMessageException>(() =>
            NativeMessageProtocol.ReadMessageAsync(new MemoryStream(header)));

        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Writing_An_Oversized_Message_Is_Refused()
    {
        var payload = new string('x', NativeMessageProtocol.MaxMessageBytes + 10);

        await Assert.ThrowsAsync<NativeMessageException>(() =>
            NativeMessageProtocol.WriteMessageAsync(new MemoryStream(), payload));
    }

    [Fact]
    public async Task Invalid_Utf8_Is_Rejected_Instead_Of_Producing_Replacement_Characters()
    {
        var body = new byte[] { 0xFF, 0xFE, 0xFD };
        var framed = BitConverter.GetBytes(body.Length).Concat(body).ToArray();

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            NativeMessageProtocol.ReadMessageAsync(new MemoryStream(framed)));
    }
}
