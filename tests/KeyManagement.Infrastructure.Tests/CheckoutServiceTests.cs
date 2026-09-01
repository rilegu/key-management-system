using KeyManagement.Application.Abstractions;
using KeyManagement.Application.Custody;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using KeyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// Custody decisions over real persistence, including every reason a request is refused.
/// </summary>
/// <remarks>
/// The refusal cases matter more than the permitted one. An authorization check that never
/// says no passes any test that only walks the happy path.
/// </remarks>
public sealed class CheckoutServiceTests
{
    private sealed record Fixture(
        TemporaryDatabase Database,
        UserId UserId,
        AssetId AssetId,
        AssetGroupId GroupId,
        AssetGroupId OtherGroupId);

    private static async Task<Fixture> ArrangeAsync(
        Permissions permissions = Permissions.CheckoutAsset,
        bool grantGroup = true,
        bool assignSlot = true,
        UserStatus status = UserStatus.Active)
    {
        var database = await TemporaryDatabase.CreateAsync();

        UserId userId = default;
        AssetId assetId = default;
        AssetGroupId groupId = default;
        AssetGroupId otherGroupId = default;

        await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            var other = new AssetGroup("Vehicles");
            var asset = new Asset("PR-001", "Boiler house main door", group.Id);
            var cabinet = new Cabinet("Reception", "Ground floor");
            var slot = cabinet.AddSlot("A01");

            if (assignSlot)
            {
                slot.Assign(asset.Id);
            }

            var role = new Role("Tester", permissions);
            var user = new User("jsmith", "J Smith", "hash");
            user.Grant(role);
            if (grantGroup)
            {
                user.GrantGroup(group.Id);
            }

            user.SetStatus(status);

            context.AssetGroups.AddRange(group, other);
            context.Assets.Add(asset);
            context.Cabinets.Add(cabinet);
            context.Roles.Add(role);
            context.Users.Add(user);
            await context.SaveChangesAsync();

            userId = user.Id;
            assetId = asset.Id;
            groupId = group.Id;
            otherGroupId = other.Id;
        });

        return new Fixture(database, userId, assetId, groupId, otherGroupId);
    }

    private static async Task<CommandResult<CheckoutSummary>> RequestAsync(Fixture fixture)
    {
        await using var scope = fixture.Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CheckoutService>()
            .RequestAsync(
                new CheckoutRequest(fixture.AssetId.Value, DueAt: null),
                fixture.UserId,
                CorrelationId.New());
    }

    [Fact]
    public async Task A_permitted_request_releases_the_asset_and_records_why()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        Assert.True(result.Success);
        Assert.Equal(CheckoutState.Pending.ToString(), result.State);
        Assert.NotNull(result.Data);

        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);

            // Pending, not CheckedOut: the cabinet has not confirmed the asset was taken.
            Assert.Equal(AssetCustodyState.CheckoutPending, asset.CustodyState);

            var types = await context.AuditEvents
                .Where(e => e.CorrelationId == new CorrelationId(result.CorrelationId))
                .Select(e => e.Type)
                .ToListAsync();

            Assert.Contains(AuditEventType.CheckoutRequested, types);
            Assert.Contains(AuditEventType.CheckoutAuthorized, types);
        });
    }

    [Fact]
    public async Task A_holder_without_the_permission_is_refused()
    {
        var fixture = await ArrangeAsync(permissions: Permissions.ViewAudit);
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        Assert.False(result.Success);
        Assert.Contains("permission", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_holder_without_the_group_is_refused()
    {
        var fixture = await ArrangeAsync(grantGroup: false);
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        Assert.False(result.Success);
        Assert.Contains("group", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_suspended_holder_is_refused_before_anything_else_is_considered()
    {
        var fixture = await ArrangeAsync(status: UserStatus.Suspended);
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        Assert.False(result.Success);
        Assert.Contains("not active", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_asset_in_no_slot_is_refused_because_no_cabinet_can_release_it()
    {
        var fixture = await ArrangeAsync(assignSlot: false);
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        Assert.False(result.Success);
        Assert.Contains("slot", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_asset_already_out_is_refused()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var first = await RequestAsync(fixture);
        Assert.True(first.Success);

        var second = await RequestAsync(fixture);

        Assert.False(second.Success);
        Assert.Contains("CheckoutPending", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uncertain_asset_must_be_reconciled_before_it_can_be_taken()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);
            asset.MarkUnknown();
            await context.SaveChangesAsync();
        });

        var result = await RequestAsync(fixture);

        Assert.False(result.Success);
        Assert.Contains("not confirmed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refusal_is_recorded_as_a_checkout_and_an_audit_entry()
    {
        // A trail that keeps only successes cannot tell "nothing happened" from "it was
        // refused", and the difference is usually why someone is reading it.
        var fixture = await ArrangeAsync(grantGroup: false);
        await using var _ = fixture.Database;

        var result = await RequestAsync(fixture);

        await fixture.Database.WithContextAsync(async context =>
        {
            var denied = await context.Checkouts
                .SingleAsync(c => c.CorrelationId == new CorrelationId(result.CorrelationId));
            Assert.Equal(CheckoutState.Denied, denied.State);
            Assert.NotNull(denied.DenialReason);

            var refusal = await context.AuditEvents
                .SingleAsync(e => e.CorrelationId == new CorrelationId(result.CorrelationId)
                                  && e.Type == AuditEventType.CheckoutDenied);
            Assert.Contains("PR-001", refusal.Summary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_refused_request_leaves_the_asset_exactly_where_it_was()
    {
        var fixture = await ArrangeAsync(grantGroup: false);
        await using var _ = fixture.Database;

        await RequestAsync(fixture);

        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);
            Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
        });
    }

    [Fact]
    public async Task Returning_starts_a_return_rather_than_completing_one()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var request = await RequestAsync(fixture);
        var checkoutId = new CheckoutId(request.Data!.Id);

        // The cabinet confirms the asset was taken; until sprint five that is done here.
        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);
            var checkout = await context.Checkouts.SingleAsync(c => c.Id == checkoutId);
            asset.ConfirmTaken();
            checkout.ConfirmTaken(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        });

        CommandResult<CheckoutSummary> result;
        await using (var scope = fixture.Database.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<CheckoutService>()
                .ReturnAsync(checkoutId, fixture.UserId, CorrelationId.New());
        }

        Assert.True(result.Success);

        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);

            // Not Available. The asset is not back until the cabinet says so.
            Assert.Equal(AssetCustodyState.ReturnPending, asset.CustodyState);
        });
    }

    [Fact]
    public async Task A_checkout_that_never_became_active_cannot_be_returned()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var request = await RequestAsync(fixture);

        await using var scope = fixture.Database.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<CheckoutService>()
            .ReturnAsync(new CheckoutId(request.Data!.Id), fixture.UserId, CorrelationId.New());

        Assert.False(result.Success);
        Assert.Contains("cannot be returned", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
