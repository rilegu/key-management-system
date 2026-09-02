using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using KeyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// Every read projection, run against SQLite.
/// </summary>
/// <remarks>
/// These exist because a projection that cannot be translated fails only when it runs. A
/// nested subquery compiles perfectly and then throws on the first request, so each of these
/// is executed here rather than trusted.
/// </remarks>
public sealed class CustodyQueriesTests
{
    private static async Task<TemporaryDatabase> SeededAsync()
    {
        var database = await TemporaryDatabase.CreateAsync();

        await using var scope = database.CreateScope();
        await TemporaryDatabase.Resolve<DatabaseSeeder>(scope).SeedAsync("correct horse battery staple", "cabinet-secret");

        return database;
    }

    private static async Task<T> QueryAsync<T>(
        TemporaryDatabase database,
        Func<ICustodyQueries, Task<T>> query)
    {
        await using var scope = database.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ICustodyQueries>());
    }

    [Fact]
    public async Task Listing_assets_flattens_group_cabinet_and_slot_onto_each_one()
    {
        await using var database = await SeededAsync();

        var assets = await QueryAsync(database, q => q.ListAssetsAsync());

        Assert.Equal(5, assets.Count);

        var first = assets.First(a => a.Reference == "PR-001");
        Assert.Equal("Plant room", first.AssetGroupName);
        Assert.Equal("Reception", first.CabinetName);
        Assert.Equal("A01", first.SlotPosition);
        Assert.Equal(nameof(AssetCustodyState.Available), first.CustodyState);
    }

    [Fact]
    public async Task Listing_assets_can_be_narrowed_to_one_group()
    {
        await using var database = await SeededAsync();

        var all = await QueryAsync(database, q => q.ListAssetsAsync());
        var group = all.First(a => a.AssetGroupName == "Vehicles").AssetGroupId;

        var vehicles = await QueryAsync(database, q => q.ListAssetsAsync(new AssetGroupId(group)));

        Assert.Equal(2, vehicles.Count);
        Assert.All(vehicles, a => Assert.Equal("Vehicles", a.AssetGroupName));
    }

    [Fact]
    public async Task Listing_cabinets_counts_their_slots()
    {
        await using var database = await SeededAsync();

        var cabinets = await QueryAsync(database, q => q.ListCabinetsAsync());

        var reception = Assert.Single(cabinets);
        Assert.Equal("Reception", reception.Name);
        Assert.Equal(10, reception.SlotCount);
        Assert.Equal(nameof(Domain.Cabinets.CabinetStatus.NeverConnected), reception.Status);
    }

    [Fact]
    public async Task A_cabinet_snapshot_lists_every_slot_in_position_order()
    {
        await using var database = await SeededAsync();

        var cabinets = await QueryAsync(database, q => q.ListCabinetsAsync());
        var snapshot = await QueryAsync(
            database,
            q => q.GetCabinetSnapshotAsync(new CabinetId(cabinets[0].Id)));

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.Slots.Count);
        Assert.Equal("A01", snapshot.Slots[0].Position);
        Assert.Equal("PR-001", snapshot.Slots[0].AssetReference);

        // Five slots are unassigned, and an unassigned slot names no asset.
        Assert.Equal(5, snapshot.Slots.Count(s => s.AssetId is null));
        Assert.All(snapshot.Slots.Where(s => s.AssetId is null), s => Assert.Null(s.AssetReference));
    }

    [Fact]
    public async Task An_unknown_cabinet_has_no_snapshot()
    {
        await using var database = await SeededAsync();

        var snapshot = await QueryAsync(
            database, q => q.GetCabinetSnapshotAsync(CabinetId.New()));

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task The_audit_search_applies_every_filter()
    {
        await using var database = await SeededAsync();

        await database.WithContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            var asset = await context.Assets.FirstAsync();
            context.AuditEvents.AddRange(
                new Domain.Auditing.AuditEvent(
                    Domain.Auditing.AuditEventType.CheckoutDenied,
                    now.AddHours(-2),
                    CorrelationId.New(),
                    "Refused.").About(asset.Id),
                new Domain.Auditing.AuditEvent(
                    Domain.Auditing.AuditEventType.SignInSucceeded,
                    now,
                    CorrelationId.New(),
                    "Signed in."));
            await context.SaveChangesAsync();
        });

        var byType = await QueryAsync(
            database, q => q.SearchAuditAsync(new AuditQuery(Type: "CheckoutDenied")));
        Assert.Single(byType);

        var recent = await QueryAsync(
            database,
            q => q.SearchAuditAsync(new AuditQuery(From: DateTimeOffset.UtcNow.AddHours(-1))));
        Assert.Single(recent);

        var capped = await QueryAsync(database, q => q.SearchAuditAsync(new AuditQuery(Take: 1)));
        Assert.Single(capped);
    }

    [Fact]
    public async Task The_audit_search_will_not_return_more_than_its_cap()
    {
        // The trail only grows, so an unbounded query is one that eventually hangs.
        await using var database = await SeededAsync();

        var asked = await QueryAsync(
            database, q => q.SearchAuditAsync(new AuditQuery(Take: int.MaxValue)));

        Assert.True(asked.Count <= CustodyQueries.MaximumAuditResults);
    }

    [Fact]
    public async Task The_dashboard_gathers_cabinets_checkouts_uncertain_assets_and_events()
    {
        await using var database = await SeededAsync();

        await database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.FirstAsync();
            asset.MarkUnknown();
            await context.SaveChangesAsync();
        });

        var dashboard = await QueryAsync(database, q => q.GetDashboardAsync());

        Assert.Single(dashboard.Cabinets);
        Assert.Empty(dashboard.ActiveCheckouts);
        Assert.Single(dashboard.UncertainAssets);
        Assert.Equal(nameof(AssetCustodyState.Unknown), dashboard.UncertainAssets[0].CustodyState);
    }
}
