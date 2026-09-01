using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Domain.Auditing;

/// <summary>
/// One immutable entry in the audit trail.
/// </summary>
/// <remarks>
/// Every property is set at construction and never changes. Append-only is enforced here as
/// well as by the absence of an update path, because a type that cannot express an amendment
/// cannot accidentally be given one.
/// </remarks>
public sealed class AuditEvent
{
    private AuditEvent()
    {
        Summary = string.Empty;
    }

    /// <summary>Records something that happened.</summary>
    /// <param name="type">What kind of thing it was.</param>
    /// <param name="occurredAt">When it happened, UTC.</param>
    /// <param name="correlationId">Ties it to the command that caused it.</param>
    /// <param name="summary">One line, written for whoever reads the trail later.</param>
    public AuditEvent(
        AuditEventType type,
        DateTimeOffset occurredAt,
        CorrelationId correlationId,
        string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Id = AuditEventId.New();
        Type = type;
        OccurredAt = occurredAt;
        CorrelationId = correlationId;
        Summary = summary;
    }

    /// <summary>Identifies this record.</summary>
    public AuditEventId Id { get; private set; }

    /// <summary>What kind of thing happened.</summary>
    public AuditEventType Type { get; private set; }

    /// <summary>When it happened, UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Ties this record to the command that caused it, and to any device command it produced.</summary>
    public CorrelationId CorrelationId { get; private set; }

    /// <summary>One line, written for whoever reads the trail later.</summary>
    public string Summary { get; private set; }

    /// <summary>The holder involved, when there was one.</summary>
    public UserId? UserId { get; private set; }

    /// <summary>The asset involved, when there was one.</summary>
    public AssetId? AssetId { get; private set; }

    /// <summary>The cabinet involved, when there was one.</summary>
    public CabinetId? CabinetId { get; private set; }

    /// <summary>Names the holder this record concerns.</summary>
    /// <param name="userId">The holder.</param>
    /// <returns>This record, for chaining at the call site.</returns>
    public AuditEvent About(UserId userId)
    {
        UserId = userId;
        return this;
    }

    /// <summary>Names the asset this record concerns.</summary>
    /// <param name="assetId">The asset.</param>
    /// <returns>This record, for chaining at the call site.</returns>
    public AuditEvent About(AssetId assetId)
    {
        AssetId = assetId;
        return this;
    }

    /// <summary>Names the cabinet this record concerns.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <returns>This record, for chaining at the call site.</returns>
    public AuditEvent About(CabinetId cabinetId)
    {
        CabinetId = cabinetId;
        return this;
    }
}
