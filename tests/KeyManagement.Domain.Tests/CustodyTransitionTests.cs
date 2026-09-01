using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Domain.Tests;

/// <summary>
/// Exercises the whole custody state machine, both directions: every move the table allows is
/// allowed, and every move it does not is refused.
/// </summary>
/// <remarks>
/// The refusal half matters more than the acceptance half. A state machine that permits
/// everything passes any test that only checks the happy path.
/// </remarks>
public sealed class CustodyTransitionTests
{
    private static readonly AssetCustodyState[] AllAssetStates = Enum.GetValues<AssetCustodyState>();
    private static readonly CheckoutState[] AllCheckoutStates = Enum.GetValues<CheckoutState>();

    public static TheoryData<AssetCustodyState, AssetCustodyState> LegalAssetMoves()
    {
        var data = new TheoryData<AssetCustodyState, AssetCustodyState>();
        foreach (var from in AllAssetStates)
        {
            foreach (var to in CustodyTransitions.AllowedFrom(from))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<AssetCustodyState, AssetCustodyState> IllegalAssetMoves()
    {
        var data = new TheoryData<AssetCustodyState, AssetCustodyState>();
        foreach (var from in AllAssetStates)
        {
            var allowed = CustodyTransitions.AllowedFrom(from);
            foreach (var to in AllAssetStates.Where(s => !allowed.Contains(s)))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<CheckoutState, CheckoutState> IllegalCheckoutMoves()
    {
        var data = new TheoryData<CheckoutState, CheckoutState>();
        foreach (var from in AllCheckoutStates)
        {
            var allowed = CustodyTransitions.AllowedFrom(from);
            foreach (var to in AllCheckoutStates.Where(s => !allowed.Contains(s)))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LegalAssetMoves))]
    public void Allowed_asset_moves_are_legal(AssetCustodyState from, AssetCustodyState to) =>
        Assert.True(CustodyTransitions.IsLegal(from, to));

    [Theory]
    [MemberData(nameof(IllegalAssetMoves))]
    public void Every_other_asset_move_is_refused(AssetCustodyState from, AssetCustodyState to)
    {
        Assert.False(CustodyTransitions.IsLegal(from, to));
        Assert.Throws<InvalidCustodyTransitionException>(
            () => CustodyTransitions.EnsureLegal(from, to, AssetId.New()));
    }

    [Theory]
    [MemberData(nameof(IllegalCheckoutMoves))]
    public void Every_other_checkout_move_is_refused(CheckoutState from, CheckoutState to)
    {
        Assert.False(CustodyTransitions.IsLegal(from, to));
        Assert.Throws<InvalidCustodyTransitionException>(
            () => CustodyTransitions.EnsureLegal(from, to, CheckoutId.New()));
    }

    [Theory]
    [InlineData(AssetCustodyState.Available)]
    [InlineData(AssetCustodyState.CheckoutPending)]
    [InlineData(AssetCustodyState.CheckedOut)]
    [InlineData(AssetCustodyState.ReturnPending)]
    [InlineData(AssetCustodyState.Faulted)]
    [InlineData(AssetCustodyState.Unknown)]
    public void A_state_cannot_move_to_itself(AssetCustodyState state) =>
        Assert.False(CustodyTransitions.IsLegal(state, state));

    [Theory]
    [InlineData(CheckoutState.Denied)]
    [InlineData(CheckoutState.Returned)]
    [InlineData(CheckoutState.Abandoned)]
    public void Settled_checkouts_are_terminal(CheckoutState settled) =>
        Assert.Empty(CustodyTransitions.AllowedFrom(settled));

    [Theory]
    [InlineData(AssetCustodyState.Available)]
    [InlineData(AssetCustodyState.CheckoutPending)]
    [InlineData(AssetCustodyState.CheckedOut)]
    [InlineData(AssetCustodyState.ReturnPending)]
    public void Any_definite_state_can_become_uncertain(AssetCustodyState from)
    {
        // A cabinet can fault or fall silent at any point, so no definite state may be a
        // dead end for the uncertain ones.
        Assert.True(CustodyTransitions.IsLegal(from, AssetCustodyState.Faulted));
        Assert.True(CustodyTransitions.IsLegal(from, AssetCustodyState.Unknown));
    }
}
