namespace KeyManagement.Devices.Protocol;

/// <summary>
/// The fixed shape of the wire.
/// </summary>
/// <remarks>
/// TCP is a byte stream with no message boundaries, so framing is ours to define: a 4-byte
/// big-endian length, a 1-byte message type, then a JSON payload. The length covers the type
/// byte and the payload.
/// </remarks>
public static class ProtocolLimits
{
    /// <summary>Bytes carrying the length of the rest of the frame.</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>The largest frame that will be read.</summary>
    /// <remarks>
    /// A bad or hostile length must not be able to make the server allocate arbitrarily, so it
    /// is checked before a buffer is taken. Nothing this protocol sends comes close: the
    /// largest realistic frame is a snapshot of a full cabinet.
    /// </remarks>
    public const int MaxFrameLength = 64 * 1024;

    /// <summary>
    /// The protocol version this build speaks.
    /// </summary>
    /// <remarks>
    /// Raised to 2 when the shared secret left <c>Hello</c> and the link became mutually
    /// authenticated. A version 1 cabinet has no certificate, so there is nothing to be
    /// compatible with — the server refuses it rather than falling back.
    /// </remarks>
    public const int Version = 2;
}

/// <summary>
/// What a frame carries.
/// </summary>
/// <remarks>
/// Explicit numbers, never reordered. A cabinet in the field may be running an older build than
/// the server, and renumbering would silently change what every one of them means.
/// </remarks>
public enum MessageType : byte
{
    /// <summary>Cabinet identifies itself and asks to attach.</summary>
    Hello = 1,

    /// <summary>Server accepts or refuses, and says what it last applied.</summary>
    HelloAck = 2,

    /// <summary>Cabinet is still there.</summary>
    Heartbeat = 3,

    /// <summary>Server asking whether the cabinet is still there.</summary>
    Ping = 4,

    /// <summary>A position changed.</summary>
    SlotStateChanged = 5,

    /// <summary>Outcome of a command, echoing its correlation id.</summary>
    CommandOutcome = 6,

    /// <summary>Events buffered while disconnected, replayed in order.</summary>
    EventBatch = 7,

    /// <summary>Server instructs the cabinet to release a position.</summary>
    UnlockSlot = 8,

    /// <summary>Server asks for the state of every position.</summary>
    RequestSnapshot = 9,

    /// <summary>Cabinet reports the state of every position.</summary>
    Snapshot = 10,

    /// <summary>Someone at the cabinet keypad asking for an item.</summary>
    AccessRequest = 11,

    /// <summary>The server's answer to the keypad.</summary>
    AccessResult = 12,
}

/// <summary>
/// One frame, as read off the wire.
/// </summary>
/// <param name="Type">What the frame carries.</param>
/// <param name="Payload">The JSON body, still encoded.</param>
public readonly record struct ProtocolFrame(MessageType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Raised when the other end sends something this protocol cannot accept.
/// </summary>
/// <remarks>
/// Always fatal to the connection. A peer that has broken framing cannot be resynchronised by
/// guessing where the next frame starts, so the connection is closed and the cabinet reconnects.
/// </remarks>
public sealed class ProtocolException : Exception
{
    /// <summary>Creates an exception with no detail.</summary>
    public ProtocolException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What was wrong with the frame.</param>
    public ProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    /// <param name="message">What was wrong with the frame.</param>
    /// <param name="innerException">The cause.</param>
    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
