namespace KeyManagement.Contracts;

/// <summary>One entry in the audit trail.</summary>
/// <param name="Id">Identifies the record.</param>
/// <param name="Type">What kind of thing happened.</param>
/// <param name="OccurredAt">When it happened, UTC.</param>
/// <param name="CorrelationId">Gathers every record produced by one command.</param>
/// <param name="Summary">One line, written for whoever reads the trail.</param>
/// <param name="UserId">The holder involved, when there was one.</param>
/// <param name="AssetId">The asset involved, when there was one.</param>
/// <param name="CabinetId">The cabinet involved, when there was one.</param>
public sealed record AuditEventSummary(
    Guid Id,
    string Type,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string Summary,
    Guid? UserId,
    Guid? AssetId,
    Guid? CabinetId);

/// <summary>How to narrow an audit search.</summary>
/// <param name="From">Earliest moment to include, UTC.</param>
/// <param name="To">Latest moment to include, UTC.</param>
/// <param name="UserId">Only records about this holder.</param>
/// <param name="AssetId">Only records about this asset.</param>
/// <param name="Type">Only records of this kind.</param>
/// <param name="Take">How many to return. Capped by the server, because the trail only grows.</param>
public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? UserId = null,
    Guid? AssetId = null,
    string? Type = null,
    int Take = 100);

/// <summary>What the dashboard shows at a glance.</summary>
/// <param name="Cabinets">Every cabinet and its link status.</param>
/// <param name="ActiveCheckouts">What is currently out.</param>
/// <param name="UncertainAssets">Assets whose whereabouts the server cannot establish.</param>
/// <param name="RecentEvents">The most recent audit records.</param>
public sealed record DashboardSummary(
    IReadOnlyList<CabinetSummary> Cabinets,
    IReadOnlyList<CheckoutSummary> ActiveCheckouts,
    IReadOnlyList<AssetSummary> UncertainAssets,
    IReadOnlyList<AuditEventSummary> RecentEvents);

/// <summary>Something an operator is expected to look at.</summary>
/// <param name="Id">Identifies the alarm.</param>
/// <param name="Type">What it is about.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Status">Whether anyone has dealt with it.</param>
/// <param name="Summary">One line for whoever deals with it.</param>
/// <param name="RaisedAt">When it was raised, UTC.</param>
/// <param name="CorrelationId">Ties it to the audit records around it.</param>
/// <param name="AssetId">The item involved, when there was one.</param>
/// <param name="AssetReference">That item's label.</param>
/// <param name="CabinetId">The cabinet involved, when there was one.</param>
/// <param name="AcknowledgedAt">When it was acknowledged, UTC.</param>
/// <param name="AcknowledgedBy">Who acknowledged it.</param>
public sealed record AlarmSummary(
    Guid Id,
    string Type,
    string Severity,
    string Status,
    string Summary,
    DateTimeOffset RaisedAt,
    Guid CorrelationId,
    Guid? AssetId,
    string? AssetReference,
    Guid? CabinetId,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy);
