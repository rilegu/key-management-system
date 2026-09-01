using KeyManagement.Domain;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Domain.Tests;

/// <summary>
/// The checkout record's own lifecycle, which is separate from the asset's custody state.
/// </summary>
public sealed class CheckoutTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static Checkout Authorized(DateTimeOffset? dueAt = null) =>
        Checkout.Authorize(
            AssetId.New(),
            UserId.New(),
            CabinetId.New(),
            SlotId.New(),
            CorrelationId.New(),
            Now,
            dueAt);

    private static Checkout Denied(string reason = "Not in a group you may check out from.") =>
        Checkout.Deny(
            AssetId.New(),
            UserId.New(),
            CabinetId.New(),
            SlotId.New(),
            CorrelationId.New(),
            Now,
            reason);

    [Fact]
    public void A_refusal_is_recorded_with_its_reason_and_is_settled_immediately()
    {
        var checkout = Denied("Asset is already checked out.");

        Assert.Equal(CheckoutState.Denied, checkout.State);
        Assert.Equal("Asset is already checked out.", checkout.DenialReason);
        Assert.True(checkout.IsSettled);
    }

    [Fact]
    public void A_refusal_cannot_later_become_active()
    {
        var checkout = Denied();

        Assert.Throws<InvalidCustodyTransitionException>(() => checkout.ConfirmTaken(Now));
    }

    [Fact]
    public void The_normal_path_runs_from_pending_to_returned()
    {
        var checkout = Authorized();
        Assert.Equal(CheckoutState.Pending, checkout.State);
        Assert.False(checkout.IsSettled);

        checkout.ConfirmTaken(Now.AddMinutes(1));
        Assert.Equal(CheckoutState.Active, checkout.State);
        Assert.Equal(Now.AddMinutes(1), checkout.TakenAt);

        checkout.ConfirmReturned(Now.AddHours(3));
        Assert.Equal(CheckoutState.Returned, checkout.State);
        Assert.Equal(Now.AddHours(3), checkout.ReturnedAt);
        Assert.True(checkout.IsSettled);
    }

    [Fact]
    public void An_uncollected_authorization_is_abandoned_not_returned()
    {
        var checkout = Authorized();

        checkout.Abandon();

        Assert.Equal(CheckoutState.Abandoned, checkout.State);
        Assert.True(checkout.IsSettled);
        Assert.Null(checkout.TakenAt);
    }

    [Fact]
    public void An_overdue_checkout_can_still_be_returned()
    {
        var checkout = Authorized(Now.AddHours(2));
        checkout.ConfirmTaken(Now);

        checkout.MarkOverdue();
        Assert.Equal(CheckoutState.Overdue, checkout.State);

        checkout.ConfirmReturned(Now.AddHours(5));
        Assert.Equal(CheckoutState.Returned, checkout.State);
    }

    [Fact]
    public void A_returned_checkout_is_terminal()
    {
        var checkout = Authorized();
        checkout.ConfirmTaken(Now);
        checkout.ConfirmReturned(Now.AddHours(1));

        Assert.Throws<InvalidCustodyTransitionException>(checkout.MarkOverdue);
    }

    [Fact]
    public void A_pending_checkout_is_not_overdue_however_late_it_gets()
    {
        // Nothing has been taken yet, so nothing is late. Only the sweep's own timeout
        // applies here, and that abandons rather than flags.
        var checkout = Authorized(Now.AddMinutes(5));

        Assert.False(checkout.IsOverdueAt(Now.AddDays(30)));
    }

    [Fact]
    public void An_open_ended_checkout_never_becomes_overdue()
    {
        var checkout = Authorized(dueAt: null);
        checkout.ConfirmTaken(Now);

        Assert.False(checkout.IsOverdueAt(Now.AddDays(365)));
    }

    [Fact]
    public void An_active_checkout_is_overdue_once_past_its_due_time()
    {
        var checkout = Authorized(Now.AddHours(2));
        checkout.ConfirmTaken(Now);

        Assert.False(checkout.IsOverdueAt(Now.AddHours(1)));
        Assert.True(checkout.IsOverdueAt(Now.AddHours(3)));
    }
}
