namespace KeyManagement.Devices.Protocol;

// The message set. Cabinets identify themselves by name rather than by identifier: a cabinet is
// enrolled by a person who typed a name, and it has no way to learn a database key.

/// <summary>A cabinet asking to attach.</summary>
/// <param name="CabinetName">The name it was enrolled under.</param>
/// <param name="FirmwareVersion">What it is running.</param>
/// <param name="ProtocolVersion">The wire version it speaks.</param>
/// <param name="LastSequenceSent">
/// The highest sequence number it has sent. The server answers with what it actually applied, and
/// the difference is what the cabinet replays.
/// </param>
/// <remarks>
/// Carries no credential. The connection is already mutually authenticated by the time this
/// arrives, so a secret in here would add nothing and be one more thing to leak.
/// <paramref name="CabinetName"/> is a claim, checked against the certificate that was actually
/// presented.
/// </remarks>
public sealed record Hello(
    string CabinetName,
    string FirmwareVersion,
    int ProtocolVersion,
    long LastSequenceSent);

/// <summary>The server's answer to <see cref="Hello"/>.</summary>
/// <param name="Accepted">Whether the cabinet may attach.</param>
/// <param name="SessionId">Identifies this attachment in the audit trail.</param>
/// <param name="LastAppliedSequence">
/// The highest sequence the server has applied from this cabinet. Anything above it that the
/// cabinet still holds should be replayed.
/// </param>
/// <param name="Reason">Why it was refused, when it was.</param>
public sealed record HelloAck(
    bool Accepted,
    Guid SessionId,
    long LastAppliedSequence,
    string? Reason);

/// <summary>A cabinet reporting that it is still connected.</summary>
/// <param name="At">When it was sent, by the cabinet's clock.</param>
public sealed record Heartbeat(DateTimeOffset At);

/// <summary>The server asking whether a quiet cabinet is still there.</summary>
/// <param name="At">When it was sent.</param>
public sealed record Ping(DateTimeOffset At);

/// <summary>A position changed.</summary>
/// <param name="Sequence">Per-cabinet, monotonic. The server discards anything it has already applied.</param>
/// <param name="Position">Which position.</param>
/// <param name="State">What it changed to: Occupied, Empty, Unlocked or Faulted.</param>
/// <param name="At">When the cabinet observed it.</param>
public sealed record SlotStateChanged(
    long Sequence,
    string Position,
    string State,
    DateTimeOffset At);

/// <summary>Events a cabinet buffered while it could not reach the server.</summary>
/// <param name="Events">In sequence order, oldest first.</param>
public sealed record EventBatch(IReadOnlyList<SlotStateChanged> Events);

/// <summary>What became of a command.</summary>
/// <param name="CorrelationId">The command's identifier, echoed back.</param>
/// <param name="Success">Whether the cabinet carried it out.</param>
/// <param name="Reason">Why not, when it did not.</param>
/// <param name="At">When the cabinet finished with it.</param>
public sealed record CommandOutcome(
    Guid CorrelationId,
    bool Success,
    string? Reason,
    DateTimeOffset At);

/// <summary>Release a position so its item can be taken.</summary>
/// <param name="CorrelationId">Ties this command to the request that caused it and to its result.</param>
/// <param name="Position">Which position to release.</param>
/// <param name="OpenFor">How long the position stays released before it locks again.</param>
public sealed record UnlockSlot(Guid CorrelationId, string Position, TimeSpan OpenFor);

/// <summary>Asks a cabinet to report every position.</summary>
/// <param name="CorrelationId">Ties the request to the snapshot that answers it.</param>
/// <remarks>
/// Sent when the server cannot account for what a cabinet has been doing: a sequence gap on
/// replay means events were lost rather than merely delayed, and only a full report resolves it.
/// </remarks>
public sealed record RequestSnapshot(Guid CorrelationId);

/// <summary>A cabinet reporting every position it holds.</summary>
/// <param name="CorrelationId">The request this answers.</param>
/// <param name="Sequence">The cabinet's sequence at the moment the snapshot was taken.</param>
/// <param name="Slots">Every position.</param>
public sealed record Snapshot(
    Guid CorrelationId,
    long Sequence,
    IReadOnlyList<SlotReport> Slots);

/// <summary>One position within a snapshot.</summary>
/// <param name="Position">Which position.</param>
/// <param name="State">What the cabinet sees there.</param>
/// <param name="At">When it last observed it.</param>
public sealed record SlotReport(string Position, string State, DateTimeOffset At);

/// <summary>Someone at the cabinet asking for an item.</summary>
/// <param name="CorrelationId">Ties the request to the answer and to its audit records.</param>
/// <param name="Position">The position they want.</param>
/// <param name="UserName">Who they say they are.</param>
/// <param name="Pin">The PIN they entered.</param>
/// <remarks>
/// A request, never a decision. The cabinet has a keypad, not an opinion: it forwards what was
/// typed and does nothing until the server answers.
/// </remarks>
public sealed record AccessRequest(
    Guid CorrelationId,
    string Position,
    string UserName,
    string Pin);

/// <summary>The server's answer to someone at the cabinet.</summary>
/// <param name="CorrelationId">The request this answers.</param>
/// <param name="Granted">Whether the item is being released.</param>
/// <param name="Message">One line for the cabinet display, whether granted or refused.</param>
/// <remarks>
/// A refusal carries a reason worth showing on the display. When granted, an
/// <see cref="UnlockSlot"/> follows separately, so releasing a position always travels the same
/// path whether the request came from a keypad or a workstation.
/// </remarks>
public sealed record AccessResult(Guid CorrelationId, bool Granted, string Message);
