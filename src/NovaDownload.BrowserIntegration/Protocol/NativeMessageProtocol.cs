using System.Buffers.Binary;
using System.Text;

namespace NovaDownload.BrowserIntegration.Protocol;

/// <summary>Thrown when a native messaging frame is structurally invalid.</summary>
public sealed class NativeMessageException : Exception
{
    public NativeMessageException(string message) : base(message) { }
}

/// <summary>
/// Native messaging wire format: a 4-byte little-endian length prefix followed by that many
/// UTF-8 bytes of JSON. The same framing is reused on the local pipe so the host is a pure relay.
/// </summary>
public static class NativeMessageProtocol
{
    /// <summary>Hard cap accepted for a single inbound message (browser limit is 1 MB).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Reads one framed message. Returns null on a clean end of stream.</summary>
    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var filled = 0;
        while (filled < 4)
        {
            var read = await stream.ReadAsync(header.AsMemory(filled, 4 - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return filled == 0 ? null : throw new NativeMessageException("Truncated message header.");
            filled += read;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0) throw new NativeMessageException($"Invalid message length: {length}.");
        if (length > MaxMessageBytes) throw new NativeMessageException($"Message of {length} bytes exceeds the {MaxMessageBytes} byte limit.");

        var body = new byte[length];
        filled = 0;
        while (filled < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled, length - filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new NativeMessageException("Truncated message body.");
            filled += read;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
    }

    /// <summary>Writes one framed message and flushes it so the browser sees it immediately.</summary>
    public static async Task WriteMessageAsync(Stream stream, string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxMessageBytes)
            throw new NativeMessageException($"Response of {body.Length} bytes exceeds the {MaxMessageBytes} byte limit.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
