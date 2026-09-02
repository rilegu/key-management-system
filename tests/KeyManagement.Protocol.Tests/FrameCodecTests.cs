using System.Buffers.Binary;
using System.Text;
using KeyManagement.Devices.Protocol;

namespace KeyManagement.Protocol.Tests;

/// <summary>
/// The wire format, exercised over streams that behave the way a socket does.
/// </summary>
/// <remarks>
/// The failure cases matter more than the round trip. A codec that only ever sees whole frames
/// arrive at once works perfectly in a test and falls apart on a real network.
/// </remarks>
public sealed class FrameCodecTests
{
    /// <summary>
    /// A stream that hands back one byte at a time.
    /// </summary>
    /// <remarks>
    /// This is what a socket actually does under load, and it is the condition a naive decoder
    /// gets wrong: reading four bytes and assuming four bytes arrived.
    /// </remarks>
    private sealed class DribblingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public DribblingStream(byte[] data) => _data = data;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length || buffer.Length == 0)
            {
                return 0;
            }

            buffer[0] = _data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<MemoryStream> WrittenAsync<T>(MessageType type, T message)
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, type, message);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_message_survives_the_round_trip()
    {
        var sent = new Hello("Reception", "shared-secret", "1.4.2", ProtocolLimits.Version, 41);

        var stream = await WrittenAsync(MessageType.Hello, sent);
        var frame = await FrameCodec.ReadAsync(stream);

        Assert.NotNull(frame);
        Assert.Equal(MessageType.Hello, frame.Value.Type);
        Assert.Equal(sent, FrameCodec.Decode<Hello>(frame.Value));
    }

    [Fact]
    public async Task Frames_are_read_back_in_order_from_one_stream()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, MessageType.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow));
        await FrameCodec.WriteAsync(
            stream,
            MessageType.SlotStateChanged,
            new SlotStateChanged(7, "A01", "Empty", DateTimeOffset.UtcNow));
        await FrameCodec.WriteAsync(stream, MessageType.RequestSnapshot, new RequestSnapshot(Guid.CreateVersion7()));
        stream.Position = 0;

        Assert.Equal(MessageType.Heartbeat, (await FrameCodec.ReadAsync(stream))!.Value.Type);

        var second = (await FrameCodec.ReadAsync(stream))!.Value;
        Assert.Equal(MessageType.SlotStateChanged, second.Type);
        Assert.Equal(7, FrameCodec.Decode<SlotStateChanged>(second).Sequence);

        Assert.Equal(MessageType.RequestSnapshot, (await FrameCodec.ReadAsync(stream))!.Value.Type);
        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task A_frame_arriving_one_byte_at_a_time_is_read_whole()
    {
        var sent = new SlotStateChanged(99, "B12", "Occupied", DateTimeOffset.UtcNow);
        var written = await WrittenAsync(MessageType.SlotStateChanged, sent);

        var frame = await FrameCodec.ReadAsync(new DribblingStream(written.ToArray()));

        Assert.NotNull(frame);
        Assert.Equal(sent, FrameCodec.Decode<SlotStateChanged>(frame.Value));
    }

    [Fact]
    public async Task Closing_between_frames_is_the_end_not_an_error()
    {
        // How a cabinet is expected to go away. It must be distinguishable from a cabinet that
        // vanished mid-frame, which is a fault.
        var frame = await FrameCodec.ReadAsync(new MemoryStream([]));

        Assert.Null(frame);
    }

    [Fact]
    public async Task A_connection_that_ends_inside_the_length_prefix_is_a_fault()
    {
        var stream = new MemoryStream([0x00, 0x00]);

        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task A_connection_that_ends_inside_the_body_is_a_fault()
    {
        var written = (await WrittenAsync(MessageType.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow)))
            .ToArray();

        // Everything but the last three bytes: the length promises more than arrives.
        var truncated = new MemoryStream(written[..^3]);

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(truncated));
        Assert.Contains("part-way", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task A_length_that_is_not_a_frame_is_refused(int declaredLength)
    {
        var prefix = new byte[ProtocolLimits.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, declaredLength);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(new MemoryStream(prefix)));
    }

    [Fact]
    public async Task An_oversized_length_is_refused_before_anything_is_allocated()
    {
        // Four bytes on the wire claiming two gigabytes. Refused on the length alone, so a
        // hostile peer cannot make the server reserve memory it will never fill.
        var prefix = new byte[ProtocolLimits.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, int.MaxValue);

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(new MemoryStream(prefix)));

        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_message_too_large_to_frame_is_refused_before_it_is_sent()
    {
        var enormous = new Snapshot(
            Guid.CreateVersion7(),
            1,
            [.. Enumerable.Range(0, 4000).Select(i =>
                new SlotReport($"POSITION-{i:D6}", "Occupied", DateTimeOffset.UtcNow))]);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.WriteAsync(new MemoryStream(), MessageType.Snapshot, enormous));
    }

    [Fact]
    public async Task An_unreadable_payload_is_a_protocol_fault_not_a_crash()
    {
        var body = Encoding.UTF8.GetBytes("this is not json");
        var frame = new byte[ProtocolLimits.LengthPrefixBytes + 1 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length + 1);
        frame[ProtocolLimits.LengthPrefixBytes] = (byte)MessageType.Hello;
        body.CopyTo(frame.AsSpan(ProtocolLimits.LengthPrefixBytes + 1));

        var read = await FrameCodec.ReadAsync(new MemoryStream(frame));

        Assert.NotNull(read);
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode<Hello>(read.Value));
    }

    [Fact]
    public async Task An_unknown_message_type_is_carried_rather_than_rejected()
    {
        // A newer cabinet may send something this build has never heard of. The frame still
        // decodes structurally, so the reader can log it and carry on instead of dropping a
        // connection that is otherwise healthy.
        var body = "{}"u8.ToArray();
        var frame = new byte[ProtocolLimits.LengthPrefixBytes + 1 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length + 1);
        frame[ProtocolLimits.LengthPrefixBytes] = 240;
        body.CopyTo(frame.AsSpan(ProtocolLimits.LengthPrefixBytes + 1));

        var read = await FrameCodec.ReadAsync(new MemoryStream(frame));

        Assert.NotNull(read);
        Assert.Equal((MessageType)240, read.Value.Type);
        Assert.False(Enum.IsDefined(read.Value.Type));
    }

    [Fact]
    public async Task A_replayed_batch_keeps_its_events_in_order()
    {
        var batch = new EventBatch(
        [
            new SlotStateChanged(11, "A01", "Empty", DateTimeOffset.UtcNow),
            new SlotStateChanged(12, "A02", "Occupied", DateTimeOffset.UtcNow),
            new SlotStateChanged(13, "A03", "Faulted", DateTimeOffset.UtcNow),
        ]);

        var stream = await WrittenAsync(MessageType.EventBatch, batch);
        var decoded = FrameCodec.Decode<EventBatch>((await FrameCodec.ReadAsync(stream))!.Value);

        Assert.Equal([11L, 12L, 13L], decoded.Events.Select(e => e.Sequence));
    }
}
