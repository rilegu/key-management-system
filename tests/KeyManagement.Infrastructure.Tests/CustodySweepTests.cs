using KeyManagement.Application.Alarms;
using KeyManagement.Application.Custody;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Alarms;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// The two problems that are the absence of an event rather than an event.
/// </summary>
public sealed class CustodySweepTests
{
    private sealed record Fixture(TemporaryDatabase Database, UserId UserId, AssetId AssetId);

    private static async Task<Fixture> ArrangeAsync()
    {
        var database = await TemporaryDatabase.CreateAsync();
        UserId userId = default;
        AssetId assetId = default;

        await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            var asset = new Asset("PR-001", "Boiler house main door", group.Id);
            var cabinet = new Cabinet("Reception", "Ground floor");
            cabinet.AddSlot("A01").Assign(asset.Id);

            var role = new Role("Tester", Permissions.CheckoutAsset | Permissions.AcknowledgeAlarm);
            var user = new User("jsmith", "J Smith", "hash");
            user.Grant(role);
            user.GrantGroup(group.Id);

            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            context.Cabinets.Add(cabinet);
            context.Roles.Add(role);
            context.Users.Add(user);
            await context.SaveChangesAsync();

            userId = user.Id;
            assetId = asset.Id;
        });

        return new Fixture(database, userId, assetId);
    }

    private static async Task<CheckoutId> RequestAsync(Fixture fixture, DateTimeOffset? due)
    {
        await using var scope = fixture.Database.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<CheckoutService>()
            .RequestAsync(
                new CheckoutRequest(fixture.AssetId.Value, due), fixture.UserId, CorrelationId.New());

        Assert.True(result.Success);
        return new CheckoutId(result.Data!.Id);
    }

    private static async Task<SweepOutcome> SweepAsync(Fixture fixture)
    {
        await using var scope = fixture.Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CustodySweep>().RunAsync();
    }

    private static async Task TakeAsync(Fixture fixture, CheckoutId checkoutId, DateTimeOffset at) =>
        await fixture.Database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);
            var checkout = await context.Checkouts.SingleAsync(c => c.Id == checkoutId);
            asset.ConfirmTaken();
            checkout.ConfirmTaken(at);
            await context.SaveChangesAsync();
        });

    private static async Task<AlarmId> OnlyAlarmAsync(Fixture fixture)
    {
        AlarmId id = default;
        await fixture.Database.WithContextAsync(async context =>
            id = (await context.Alarms.FirstAsync()).Id);

        return id;
    }

    [Fact]
    public async Task An_item_held_past_its_curfew_becomes_overdue_and_raises_an_alarm()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-5));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddHours(-1));

        var outcome = await SweepAsync(fixture);

        Assert.Equal(1, outcome.MarkedOverdue);

        await fixture.Database.WithContextAsync(async context =>
        {
            var checkout = await context.Checkouts.SingleAsync(c => c.Id == checkoutId);
            Assert.Equal(CheckoutState.Overdue, checkout.State);

            var alarm = await context.Alarms.SingleAsync(a => a.Type == AlarmType.OverdueItem);
            Assert.Equal(AlarmStatus.Active, alarm.Status);
            Assert.Contains("PR-001", alarm.Summary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_same_problem_raises_one_alarm_however_often_it_is_noticed()
    {
        // A sweep every thirty seconds would otherwise add a row every thirty seconds, and a
        // list that grows on its own is a list nobody reads.
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-5));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddHours(-1));

        await SweepAsync(fixture);
        await SweepAsync(fixture);
        await SweepAsync(fixture);

        await fixture.Database.WithContextAsync(async context =>
            Assert.Equal(1, await context.Alarms.CountAsync(a => a.Type == AlarmType.OverdueItem)));
    }

    [Fact]
    public async Task An_item_that_is_not_yet_due_is_left_alone()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddHours(4));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow);

        var outcome = await SweepAsync(fixture);

        Assert.Equal(0, outcome.MarkedOverdue);
        await fixture.Database.WithContextAsync(async context =>
            Assert.Empty(await context.Alarms.ToListAsync()));
    }

    [Fact]
    public async Task An_open_ended_checkout_never_becomes_overdue()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, due: null);
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(0, (await SweepAsync(fixture)).MarkedOverdue);
    }

    [Fact]
    public async Task A_release_nobody_collected_is_closed_and_the_item_freed()
    {
        // The gap the unlock window left: a position was opened, nothing came out, and the
        // request would otherwise hold the item for ever.
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, due: null);

        await fixture.Database.WithContextAsync(async context =>
        {
            var checkout = await context.Checkouts.SingleAsync(c => c.Id == checkoutId);
            context.Entry(checkout).Property(c => c.RequestedAt).CurrentValue =
                DateTimeOffset.UtcNow - CustodySweep.UncollectedAfter - TimeSpan.FromMinutes(1);
            await context.SaveChangesAsync();
        });

        var outcome = await SweepAsync(fixture);

        Assert.Equal(1, outcome.Abandoned);

        await fixture.Database.WithContextAsync(async context =>
        {
            var checkout = await context.Checkouts.SingleAsync(c => c.Id == checkoutId);
            var asset = await context.Assets.SingleAsync(a => a.Id == fixture.AssetId);

            Assert.Equal(CheckoutState.Abandoned, checkout.State);

            // Back on the board, because it never left the cabinet.
            Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
        });
    }

    [Fact]
    public async Task A_release_only_just_made_is_left_to_be_collected()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        await RequestAsync(fixture, due: null);

        Assert.Equal(0, (await SweepAsync(fixture)).Abandoned);
    }

    [Fact]
    public async Task Acknowledging_records_who_and_when()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-5));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddHours(-1));
        await SweepAsync(fixture);

        var alarmId = await OnlyAlarmAsync(fixture);

        CommandResult result;
        await using (var scope = fixture.Database.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<AlarmService>()
                .AcknowledgeAsync(alarmId, fixture.UserId, CorrelationId.New());
        }

        Assert.True(result.Success);

        await fixture.Database.WithContextAsync(async context =>
        {
            var alarm = await context.Alarms.SingleAsync(a => a.Id == alarmId);
            Assert.Equal(AlarmStatus.Acknowledged, alarm.Status);
            Assert.Equal(fixture.UserId, alarm.AcknowledgedBy);
            Assert.NotNull(alarm.AcknowledgedAt);
        });
    }

    [Fact]
    public async Task A_holder_without_the_permission_cannot_acknowledge()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        UserId bystander = default;
        await fixture.Database.WithContextAsync(async context =>
        {
            var role = new Role("Onlooker", Permissions.CheckoutAsset);
            var user = new User("onlooker", "An Onlooker", "hash");
            user.Grant(role);
            context.Roles.Add(role);
            context.Users.Add(user);
            await context.SaveChangesAsync();
            bystander = user.Id;
        });

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-5));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddHours(-1));
        await SweepAsync(fixture);

        var alarmId = await OnlyAlarmAsync(fixture);

        await using var scope = fixture.Database.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AlarmService>()
            .AcknowledgeAsync(alarmId, bystander, CorrelationId.New());

        Assert.False(result.Success);
        Assert.Contains("permission", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acknowledging_twice_keeps_the_first_person()
    {
        var fixture = await ArrangeAsync();
        await using var _ = fixture.Database;

        var checkoutId = await RequestAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-5));
        await TakeAsync(fixture, checkoutId, DateTimeOffset.UtcNow.AddHours(-1));
        await SweepAsync(fixture);

        var alarmId = await OnlyAlarmAsync(fixture);

        await using (var first = fixture.Database.CreateScope())
        {
            await first.ServiceProvider.GetRequiredService<AlarmService>()
                .AcknowledgeAsync(alarmId, fixture.UserId, CorrelationId.New());
        }

        DateTimeOffset? acknowledgedAt = null;
        await fixture.Database.WithContextAsync(async context =>
            acknowledgedAt = (await context.Alarms.SingleAsync(a => a.Id == alarmId)).AcknowledgedAt);

        await using (var second = fixture.Database.CreateScope())
        {
            var again = await second.ServiceProvider.GetRequiredService<AlarmService>()
                .AcknowledgeAsync(alarmId, fixture.UserId, CorrelationId.New());
            Assert.True(again.Success);
        }

        await fixture.Database.WithContextAsync(async context =>
        {
            var alarm = await context.Alarms.SingleAsync(a => a.Id == alarmId);
            Assert.Equal(acknowledgedAt, alarm.AcknowledgedAt);
        });
    }
}
