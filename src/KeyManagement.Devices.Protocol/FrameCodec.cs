using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyManagement.Devices.Protocol;

/// <summary>
/// Reads and writes frames on a stream.
/// </summary>
/// <remarks>
/// <para>
/// Written against <see cref="Stream"/> rather than a socket, so wrapping the connection in
/// TLS later is a change at the call site and none at all here.
/// </para>
/// <para>
/// A partial read is a normal condition on a byte stream, not a corruption. Every read here
/// insists on the exact number of bytes it needs and treats anything less as the peer having
/// gone away mid-frame.
/// </para>
/// </remarks>
public static class FrameCodec
{
    /// <summary>How payloads are encoded.</summary>
    /// <remarks>
    /// Readable in a packet capture and in a log, which is worth more on this link than the
    /// bytes a packed encoding would save. Unknown members are ignored so a newer cabinet can
    /// add a field without breaking an older server.
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes one frame.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    /// <param name="stream">Where to write it.</param>
    /// <param name="type">What the frame carries.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the frame is written and flushed.</returns>
    /// <exception cref="ProtocolException">The encoded message exceeds the frame limit.</exception>
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        MessageType type,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var bodyLength = payload.Length + 1;

        if (bodyLength > ProtocolLimits.MaxFrameLength)
        {
            throw new ProtocolException(
                $"A {type} frame of {bodyLength} bytes exceeds the {ProtocolLimits.MaxFrameLength} byte limit.");
        }

        var frame = new byte[ProtocolLimits.LengthPrefixBytes + bodyLength];
        BinaryPrimitives.WriteInt32BigEndian(frame, bodyLength);
        frame[ProtocolLimits.LengthPrefixBytes] = (byte)type;
        payload.CopyTo(frame.AsSpan(ProtocolLimits.LengthPrefixBytes + 1));

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame.</summary>
    /// <param name="stream">Where to read from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The frame, or <see langword="null"/> when the peer closed the connection cleanly between
    /// frames. Closing between frames is how a cabinet is expected to go away.
    /// </returns>
    /// <exception cref="ProtocolException">
    /// The length is impossible, the frame is empty, or the peer vanished part-way through one.
    /// </exception>
    public static async ValueTask<ProtocolFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthPrefix = new byte[ProtocolLimits.LengthPrefixBytes];
        var read = await stream
            .ReadAtLeastAsync(lengthPrefix, lengthPrefix.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            return null;
        }

        if (read < lengthPrefix.Length)
        {
            throw new ProtocolException(
                $"The connection ended after {read} bytes of a {lengthPrefix.Length} byte length prefix.");
        }

        var bodyLength = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);

        // Checked before a buffer is taken, so a bad length costs nothing.
        if (bodyLength < 1)
        {
            throw new ProtocolException($"A frame length of {bodyLength} is not a frame.");
        }

        if (bodyLength > ProtocolLimits.MaxFrameLength)
        {
            throw new ProtocolException(
                $"A frame length of {bodyLength} exceeds the {ProtocolLimits.MaxFrameLength} byte limit.");
        }

        var body = new byte[bodyLength];

        try
        {
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProtocolException(
                $"The connection ended part-way through a {bodyLength} byte frame.", exception);
        }

        return new ProtocolFrame((MessageType)body[0], body.AsMemory(1));
    }

    /// <summary>Decodes a frame's payload.</summary>
    /// <typeparam name="T">The expected message.</typeparam>
    /// <param name="frame">The frame.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ProtocolException">The payload is not the message it claims to be.</exception>
    public static T Decode<T>(ProtocolFrame frame)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Json)
                ?? throw new ProtocolException($"A {frame.Type} frame carried an empty payload.");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(
                $"A {frame.Type} frame carried a payload that could not be read.", exception);
        }
    }
}
