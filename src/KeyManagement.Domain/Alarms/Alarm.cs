using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Domain.Alarms;

/// <summary>
/// Something an operator is expected to look at.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from an audit record. The trail says what happened; an alarm says something is
/// wrong and stays visible until a person says they have seen it. Everything raised here is
/// also audited, so the trail remains the complete account and the alarm list stays short
/// enough to be read.
/// </para>
/// <para>
/// A problem raises one alarm, not one per sweep. <see cref="Scope"/> is what identifies "the
/// same problem", and the database refuses a second active alarm carrying the same one.
/// </para>
/// </remarks>
public sealed class Alarm
{
    private Alarm()
    {
        Scope = string.Empty;
        Summary = string.Empty;
    }

    private Alarm(
        AlarmType type,
        AlarmSeverity severity,
        string scope,
        string summary,
        DateTimeOffset raisedAt,
        CorrelationId correlationId)
    {
        Id = AlarmId.New();
        Type = type;
        Severity = severity;
        Scope = scope;
        Summary = summary;
        RaisedAt = raisedAt;
        CorrelationId = correlationId;
        Status = AlarmStatus.Active;
    }

    /// <summary>Identifies this alarm.</summary>
    public AlarmId Id { get; private set; }

    /// <summary>What it is about.</summary>
    public AlarmType Type { get; private set; }

    /// <summary>How much it matters.</summary>
    public AlarmSeverity Severity { get; private set; }

    /// <summary>
    /// Identifies the underlying problem, so it is raised once rather than once per check.
    /// </summary>
    /// <remarks>
    /// Unique among active alarms. An item that is overdue is one problem however many times
    /// the sweep notices it, and a list that grows a row a minute is a list nobody reads.
    /// </remarks>
    public string Scope { get; private set; }

    /// <summary>One line, written for whoever has to deal with it.</summary>
    public string Summary { get; private set; }

    /// <summary>Where it stands.</summary>
    public AlarmStatus Status { get; private set; }

    /// <summary>When it was raised, UTC.</summary>
    public DateTimeOffset RaisedAt { get; private set; }

    /// <summary>Ties it to the audit records around it.</summary>
    public CorrelationId CorrelationId { get; private set; }

    /// <summary>The holder involved, when there was one.</summary>
    public UserId? UserId { get; private set; }

    /// <summary>The item involved, when there was one.</summary>
    public AssetId? AssetId { get; private set; }

    /// <summary>The cabinet involved, when there was one.</summary>
    public CabinetId? CabinetId { get; private set; }

    /// <summary>When it was acknowledged, UTC.</summary>
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>Who acknowledged it.</summary>
    /// <remarks>
    /// Recorded, and never cleared. An alarm that was dismissed is a fact about a person as
    /// much as about the alarm.
    /// </remarks>
    public UserId? AcknowledgedBy { get; private set; }

    /// <summary>Raises an alarm.</summary>
    /// <param name="type">What it is about.</param>
    /// <param name="severity">How much it matters.</param>
    /// <param name="scope">Identifies the underlying problem.</param>
    /// <param name="summary">One line for whoever deals with it.</param>
    /// <param name="raisedAt">When, UTC.</param>
    /// <param name="correlationId">Ties it to the audit records around it.</param>
    /// <returns>The alarm.</returns>
    public static Alarm Raise(
        AlarmType type,
        AlarmSeverity severity,
        string scope,
        string summary,
        DateTimeOffset raisedAt,
        CorrelationId correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        return new Alarm(type, severity, scope, summary, raisedAt, correlationId);
    }

    /// <summary>Names the holder this alarm concerns.</summary>
    /// <param name="userId">The holder.</param>
    /// <returns>This alarm, for chaining.</returns>
    public Alarm About(UserId userId)
    {
        UserId = userId;
        return this;
    }

    /// <summary>Names the item this alarm concerns.</summary>
    /// <param name="assetId">The item.</param>
    /// <returns>This alarm, for chaining.</returns>
    public Alarm About(AssetId assetId)
    {
        AssetId = assetId;
        return this;
    }

    /// <summary>Names the cabinet this alarm concerns.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <returns>This alarm, for chaining.</returns>
    public Alarm About(CabinetId cabinetId)
    {
        CabinetId = cabinetId;
        return this;
    }

    /// <summary>Records that someone has seen it.</summary>
    /// <param name="acknowledgedBy">Who.</param>
    /// <param name="at">When, UTC.</param>
    /// <remarks>
    /// Acknowledging twice keeps the first person and the first moment. Whoever dealt with it
    /// dealt with it; a later click does not reassign that.
    /// </remarks>
    public void Acknowledge(UserId acknowledgedBy, DateTimeOffset at)
    {
        if (Status == AlarmStatus.Acknowledged)
        {
            return;
        }

        Status = AlarmStatus.Acknowledged;
        AcknowledgedBy = acknowledgedBy;
        AcknowledgedAt = at;
    }

    /// <summary>The scope identifying an overdue item.</summary>
    /// <param name="checkoutId">The checkout that is late.</param>
    /// <returns>The scope.</returns>
    public static string OverdueScope(CheckoutId checkoutId) => $"overdue:{checkoutId}";

    /// <summary>The scope identifying a release nobody collected.</summary>
    /// <param name="checkoutId">The checkout that was never picked up.</param>
    /// <returns>The scope.</returns>
    public static string UncollectedScope(CheckoutId checkoutId) => $"uncollected:{checkoutId}";

    /// <summary>The scope identifying a cabinet being unreachable.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <returns>The scope.</returns>
    public static string OfflineScope(CabinetId cabinetId) => $"offline:{cabinetId}";

    /// <summary>The scope identifying an unauthorized removal.</summary>
    /// <param name="assetId">The item that left.</param>
    /// <returns>The scope.</returns>
    public static string UnauthorizedScope(AssetId assetId) => $"unauthorized:{assetId}";

    /// <summary>The scope identifying a faulted position.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="position">The position.</param>
    /// <returns>The scope.</returns>
    public static string FaultScope(CabinetId cabinetId, string position) =>
        $"fault:{cabinetId}:{position}";
}
