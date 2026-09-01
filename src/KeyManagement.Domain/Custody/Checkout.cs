using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Domain.Custody;

/// <summary>
/// One request to take custody of an asset, and everything that became of it.
/// </summary>
/// <remarks>
/// A refused request still produces a record. "Nothing happened" and "it was refused" look
/// identical in a trail that only keeps successes, and telling them apart is usually why
/// someone is reading it.
/// </remarks>
public sealed class Checkout
{
    private Checkout()
    {
    }

    private Checkout(
        AssetId assetId,
        UserId userId,
        CabinetId cabinetId,
        SlotId slotId,
        CorrelationId correlationId,
        DateTimeOffset requestedAt)
    {
        Id = CheckoutId.New();
        AssetId = assetId;
        UserId = userId;
        CabinetId = cabinetId;
        SlotId = slotId;
        CorrelationId = correlationId;
        RequestedAt = requestedAt;
    }

    /// <summary>Identifies this checkout.</summary>
    public CheckoutId Id { get; private set; }

    /// <summary>The asset requested.</summary>
    public AssetId AssetId { get; private set; }

    /// <summary>The holder who asked.</summary>
    public UserId UserId { get; private set; }

    /// <summary>The cabinet holding the asset.</summary>
    public CabinetId CabinetId { get; private set; }

    /// <summary>The slot holding the asset.</summary>
    public SlotId SlotId { get; private set; }

    /// <summary>Ties this checkout to its audit records and to the device command it caused.</summary>
    public CorrelationId CorrelationId { get; private set; }

    /// <summary>Where the request got to.</summary>
    public CheckoutState State { get; private set; }

    /// <summary>When the holder asked, UTC.</summary>
    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>When the asset was confirmed taken, UTC.</summary>
    public DateTimeOffset? TakenAt { get; private set; }

    /// <summary>When it is due back, UTC, or <see langword="null"/> if it is not time-limited.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    /// <summary>When it was confirmed back in its slot, UTC.</summary>
    public DateTimeOffset? ReturnedAt { get; private set; }

    /// <summary>Why the request was refused, shown to the holder and kept in the record.</summary>
    public string? DenialReason { get; private set; }

    /// <summary>Whether the request reached a settled outcome that will not change.</summary>
    public bool IsSettled =>
        State is CheckoutState.Denied or CheckoutState.Returned or CheckoutState.Abandoned;

    /// <summary>Records a permitted request, awaiting collection from the cabinet.</summary>
    /// <param name="assetId">The asset requested.</param>
    /// <param name="userId">The holder who asked.</param>
    /// <param name="cabinetId">The cabinet holding it.</param>
    /// <param name="slotId">The slot holding it.</param>
    /// <param name="correlationId">Ties this to its audit records and device command.</param>
    /// <param name="requestedAt">When the holder asked, UTC.</param>
    /// <param name="dueAt">When it is due back, UTC, if it is time-limited.</param>
    /// <returns>A checkout in <see cref="CheckoutState.Pending"/>.</returns>
    public static Checkout Authorize(
        AssetId assetId,
        UserId userId,
        CabinetId cabinetId,
        SlotId slotId,
        CorrelationId correlationId,
        DateTimeOffset requestedAt,
        DateTimeOffset? dueAt = null) =>
        new(assetId, userId, cabinetId, slotId, correlationId, requestedAt)
        {
            State = CheckoutState.Pending,
            DueAt = dueAt,
        };

    /// <summary>Records a refused request. Terminal from the moment it is created.</summary>
    /// <param name="assetId">The asset requested.</param>
    /// <param name="userId">The holder who asked.</param>
    /// <param name="cabinetId">The cabinet holding it.</param>
    /// <param name="slotId">The slot holding it.</param>
    /// <param name="correlationId">Ties this to its audit records.</param>
    /// <param name="requestedAt">When the holder asked, UTC.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>A checkout in <see cref="CheckoutState.Denied"/>.</returns>
    public static Checkout Deny(
        AssetId assetId,
        UserId userId,
        CabinetId cabinetId,
        SlotId slotId,
        CorrelationId correlationId,
        DateTimeOffset requestedAt,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new Checkout(assetId, userId, cabinetId, slotId, correlationId, requestedAt)
        {
            State = CheckoutState.Denied,
            DenialReason = reason,
        };
    }

    /// <summary>Records that the cabinet confirmed the asset was taken.</summary>
    /// <param name="at">When it was taken, UTC.</param>
    public void ConfirmTaken(DateTimeOffset at)
    {
        MoveTo(CheckoutState.Active);
        TakenAt = at;
    }

    /// <summary>Records that an authorized checkout was never collected.</summary>
    public void Abandon() => MoveTo(CheckoutState.Abandoned);

    /// <summary>Records that the asset is now past its due time.</summary>
    /// <exception cref="InvalidCustodyTransitionException">The checkout is not active.</exception>
    public void MarkOverdue() => MoveTo(CheckoutState.Overdue);

    /// <summary>Records that the asset is back in its slot.</summary>
    /// <param name="at">When it was returned, UTC.</param>
    public void ConfirmReturned(DateTimeOffset at)
    {
        MoveTo(CheckoutState.Returned);
        ReturnedAt = at;
    }

    /// <summary>Whether the asset is held past its due time at a given moment.</summary>
    /// <param name="asOf">The moment to judge at, UTC.</param>
    /// <returns><see langword="true"/> when it is active, time-limited and past due.</returns>
    public bool IsOverdueAt(DateTimeOffset asOf) =>
        State == CheckoutState.Active && DueAt is { } due && asOf > due;

    private void MoveTo(CheckoutState next)
    {
        CustodyTransitions.EnsureLegal(State, next, Id);
        State = next;
    }
}
