using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Domain.Auditing;

/// <summary>
/// A message received from a cabinet, kept as reported.
/// </summary>
/// <remarks>
/// Stored separately from <see cref="AuditEvent"/> and before interpretation. When the server
/// and a cabinet disagree about what happened, this is the only record of what the cabinet
/// actually said, including messages that were discarded as duplicates.
/// </remarks>
public sealed class DeviceEvent
{
    private DeviceEvent()
    {
        Kind = string.Empty;
        Payload = string.Empty;
    }

    /// <summary>Records a message from a cabinet.</summary>
    /// <param name="cabinetId">The cabinet that sent it.</param>
    /// <param name="sequence">Its per-cabinet sequence number.</param>
    /// <param name="kind">The message type, as named by the protocol.</param>
    /// <param name="payload">The message body as received.</param>
    /// <param name="occurredAt">When the cabinet says it happened, UTC.</param>
    /// <param name="receivedAt">When the server received it, UTC.</param>
    /// <param name="applied">Whether it was applied, or discarded as already seen.</param>
    public DeviceEvent(
        CabinetId cabinetId,
        long sequence,
        string kind,
        string payload,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        bool applied)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);

        Id = DeviceEventId.New();
        CabinetId = cabinetId;
        Sequence = sequence;
        Kind = kind;
        Payload = payload;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Applied = applied;
    }

    /// <summary>Identifies this record.</summary>
    public DeviceEventId Id { get; private set; }

    /// <summary>The cabinet that sent it.</summary>
    public CabinetId CabinetId { get; private set; }

    /// <summary>Its per-cabinet sequence number.</summary>
    public long Sequence { get; private set; }

    /// <summary>The message type, as named by the protocol.</summary>
    public string Kind { get; private set; }

    /// <summary>The message body as received.</summary>
    public string Payload { get; private set; }

    /// <summary>When the cabinet says it happened, UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When the server received it, UTC. Differs from <see cref="OccurredAt"/> after a replay.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Whether it changed anything, or was discarded as already seen.</summary>
    public bool Applied { get; private set; }
}
