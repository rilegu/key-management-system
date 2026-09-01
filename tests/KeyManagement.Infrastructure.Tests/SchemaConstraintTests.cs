using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Cabinets;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// The uniqueness rules the system relies on, proved against the database rather than assumed
/// from the configuration that declares them.
/// </summary>
public sealed class SchemaConstraintTests
{
    [Fact]
    public async Task Two_assets_cannot_share_a_reference()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            context.AssetGroups.Add(group);
            context.Assets.Add(new Asset("PR-001", "Boiler house", group.Id));
            await context.SaveChangesAsync();

            context.Assets.Add(new Asset("PR-001", "A different door, same label", group.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Two_holders_cannot_share_a_user_name()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            context.Users.Add(new User("jsmith", "J Smith", "hash"));
            await context.SaveChangesAsync();

            context.Users.Add(new User("jsmith", "Someone else entirely", "hash"));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task A_cabinet_cannot_have_two_slots_in_the_same_position()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var cabinet = new Cabinet("Reception", "Ground floor");
            cabinet.AddSlot("A01");
            cabinet.AddSlot("A01");
            context.Cabinets.Add(cabinet);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task An_asset_cannot_live_in_two_slots()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            var asset = new Asset("PR-001", "Boiler house", group.Id);
            var cabinet = new Cabinet("Reception", "Ground floor");
            var first = cabinet.AddSlot("A01");
            var second = cabinet.AddSlot("A02");
            first.Assign(asset.Id);
            second.Assign(asset.Id);

            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            context.Cabinets.Add(cabinet);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Many_slots_may_be_unassigned_at_once()
    {
        // The unique index on Slots.AssetId is filtered. Without the filter SQLite would
        // treat every unassigned slot as a duplicate of the others, and a cabinet could hold
        // exactly one empty slot.
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var cabinet = new Cabinet("Reception", "Ground floor");
            for (var position = 1; position <= 10; position++)
            {
                cabinet.AddSlot($"A{position:D2}");
            }

            context.Cabinets.Add(cabinet);
            await context.SaveChangesAsync();

            var unassigned = await context.Slots.CountAsync(s => s.AssetId == null);
            Assert.Equal(10, unassigned);
        });
    }

    [Fact]
    public async Task A_cabinet_never_reuses_a_sequence_number()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var cabinet = new Cabinet("Reception", "Ground floor");
            context.Cabinets.Add(cabinet);
            await context.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            context.DeviceEvents.Add(new Domain.Auditing.DeviceEvent(
                cabinet.Id, 1, "SlotStateChanged", "{}", now, now, applied: true));
            await context.SaveChangesAsync();

            context.DeviceEvents.Add(new Domain.Auditing.DeviceEvent(
                cabinet.Id, 1, "SlotStateChanged", "{}", now, now, applied: false));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        });
    }
}
