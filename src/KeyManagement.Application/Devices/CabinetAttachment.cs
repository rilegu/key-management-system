using KeyManagement.Domain;
using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Application.Devices;

/// <summary>
/// The outcome of a cabinet trying to attach.
/// </summary>
/// <param name="Accepted">Whether it may attach.</param>
/// <param name="CabinetId">Which cabinet it is, once identified.</param>
/// <param name="SessionId">Identifies this attachment in the audit trail.</param>
/// <param name="LastAppliedSequence">
/// The highest event sequence the server has applied from it. What the cabinet still holds above
/// this is what it should replay.
/// </param>
/// <param name="Reason">Why it was refused, when it was.</param>
public sealed record CabinetAttachment(
    bool Accepted,
    CabinetId CabinetId,
    Guid SessionId,
    long LastAppliedSequence,
    string? Reason)
{
    /// <summary>Refuses an attachment.</summary>
    /// <param name="reason">Why, for the log. Not sent in enough detail to help a guesser.</param>
    /// <returns>A refusal.</returns>
    public static CabinetAttachment Refused(string reason) =>
        new(false, default, Guid.Empty, 0, reason);
}

/// <summary>
/// One position as a cabinet reports it.
/// </summary>
/// <param name="Sequence">Per-cabinet, monotonic.</param>
/// <param name="Position">Which position.</param>
/// <param name="State">What the cabinet sees there.</param>
/// <param name="At">When it observed it.</param>
public sealed record PositionReport(long Sequence, string Position, SlotState State, DateTimeOffset At);

/// <summary>
/// What the server did with a reported change.
/// </summary>
public enum ReportOutcome
{
    /// <summary>Applied, and custody may have moved.</summary>
    Applied = 0,

    /// <summary>Already seen. Discarded rather than applied twice.</summary>
    AlreadyApplied = 1,

    /// <summary>The cabinet or position is not one this server knows.</summary>
    Unknown = 2,

    /// <summary>
    /// Applied, and it did not match anything the server authorized.
    /// </summary>
    /// <remarks>
    /// A position emptying with no release behind it. Custody becomes uncertain and an alarm is
    /// raised rather than the trail quietly recording a checkout nobody asked for.
    /// </remarks>
    Unauthorized = 3,
}
