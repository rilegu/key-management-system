using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Domain.Tests;

/// <summary>
/// The custody lifecycle as an asset actually travels it, through the entity rather than the
/// transition table.
/// </summary>
public sealed class AssetCustodyTests
{
    private static Asset NewAsset() => new("PR-001", "Boiler house main door", AssetGroupId.New());

    [Fact]
    public void A_new_asset_is_available()
    {
        var asset = NewAsset();

        Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
        Assert.False(asset.IsUncertain);
    }

    [Fact]
    public void The_full_checkout_and_return_round_trip_ends_where_it_started()
    {
        var asset = NewAsset();

        asset.BeginCheckout();
        Assert.Equal(AssetCustodyState.CheckoutPending, asset.CustodyState);

        asset.ConfirmTaken();
        Assert.Equal(AssetCustodyState.CheckedOut, asset.CustodyState);

        asset.BeginReturn();
        Assert.Equal(AssetCustodyState.ReturnPending, asset.CustodyState);

        asset.ConfirmReturned();
        Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
    }

    [Fact]
    public void An_uncollected_checkout_releases_the_asset()
    {
        var asset = NewAsset();
        asset.BeginCheckout();

        asset.AbandonCheckout();

        Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
    }

    [Fact]
    public void An_incomplete_return_leaves_the_asset_with_the_holder()
    {
        var asset = NewAsset();
        asset.BeginCheckout();
        asset.ConfirmTaken();
        asset.BeginReturn();

        asset.AbandonReturn();

        Assert.Equal(AssetCustodyState.CheckedOut, asset.CustodyState);
    }

    [Fact]
    public void An_asset_cannot_be_returned_without_being_taken()
    {
        var asset = NewAsset();

        Assert.Throws<InvalidCustodyTransitionException>(asset.BeginReturn);
    }

    [Fact]
    public void An_asset_cannot_be_checked_out_twice()
    {
        var asset = NewAsset();
        asset.BeginCheckout();
        asset.ConfirmTaken();

        Assert.Throws<InvalidCustodyTransitionException>(asset.BeginCheckout);
    }

    [Fact]
    public void An_offline_cabinet_makes_custody_uncertain_rather_than_available()
    {
        var asset = NewAsset();
        asset.BeginCheckout();
        asset.ConfirmTaken();

        asset.MarkUnknown();

        Assert.Equal(AssetCustodyState.Unknown, asset.CustodyState);
        Assert.True(asset.IsUncertain);
    }

    [Fact]
    public void Reconciliation_settles_an_uncertain_asset()
    {
        var asset = NewAsset();
        asset.MarkUnknown();

        asset.Reconcile(AssetCustodyState.CheckedOut);

        Assert.Equal(AssetCustodyState.CheckedOut, asset.CustodyState);
        Assert.False(asset.IsUncertain);
    }

    [Theory]
    [InlineData(AssetCustodyState.Unknown)]
    [InlineData(AssetCustodyState.Faulted)]
    [InlineData(AssetCustodyState.CheckoutPending)]
    [InlineData(AssetCustodyState.ReturnPending)]
    public void Reconciliation_must_settle_on_a_definite_state(AssetCustodyState notDefinite)
    {
        var asset = NewAsset();
        asset.MarkFaulted();

        Assert.Throws<ArgumentOutOfRangeException>(() => asset.Reconcile(notDefinite));
    }
}
