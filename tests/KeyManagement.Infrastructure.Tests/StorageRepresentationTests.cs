using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// How values land in the file, which matters because someone will eventually read this
/// database directly during an incident.
/// </summary>
public sealed class StorageRepresentationTests
{
    [Fact]
    public async Task Custody_states_are_stored_as_names_not_numbers()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var stored = await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            var asset = new Asset("PR-001", "Boiler house", group.Id);
            asset.BeginCheckout();
            asset.ConfirmTaken();
            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            await context.SaveChangesAsync();

            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CustodyState FROM Assets LIMIT 1;";
            return (string?)await command.ExecuteScalarAsync();
        });

        Assert.Equal("CheckedOut", stored);
    }

    [Fact]
    public async Task Timestamps_are_normalised_to_utc_on_the_way_in()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var correlation = CorrelationId.New();

        // Deliberately not UTC. The rule is that the database holds UTC and only the UI
        // converts, and a caller passing local time should not be able to break it.
        var localTime = new DateTimeOffset(2026, 9, 1, 14, 30, 0, TimeSpan.FromHours(5));

        await database.WithContextAsync(async context =>
        {
            context.AuditEvents.Add(new AuditEvent(
                AuditEventType.SignInSucceeded, localTime, correlation, "Signed in."));
            await context.SaveChangesAsync();
        });

        await database.WithContextAsync(async context =>
        {
            var record = await context.AuditEvents
                .SingleAsync(e => e.CorrelationId == correlation);

            Assert.Equal(TimeSpan.Zero, record.OccurredAt.Offset);
            Assert.Equal(localTime.UtcDateTime, record.OccurredAt.UtcDateTime);
        });
    }

    [Fact]
    public async Task The_audit_trail_can_be_ordered_and_filtered_by_time()
    {
        // SQLite cannot ORDER BY its default DateTimeOffset mapping at all, and reading the
        // trail newest-first is the only thing anyone does with it. UtcDateTimeOffsetConverter
        // exists for this; without it every query below throws NotSupportedException.
        await using var database = await TemporaryDatabase.CreateAsync();
        var midday = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        await database.WithContextAsync(async context =>
        {
            context.AuditEvents.AddRange(
                new AuditEvent(AuditEventType.SignInSucceeded, midday.AddHours(2), CorrelationId.New(), "Third."),
                new AuditEvent(AuditEventType.SignInSucceeded, midday, CorrelationId.New(), "First."),

                // Same moment, written with a different offset. Ordering must follow the
                // instant, not the text as it was supplied.
                new AuditEvent(
                    AuditEventType.SignInSucceeded,
                    midday.AddHours(1).ToOffset(TimeSpan.FromHours(9)),
                    CorrelationId.New(),
                    "Second."));
            await context.SaveChangesAsync();
        });

        await database.WithContextAsync(async context =>
        {
            var newestFirst = await context.AuditEvents
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => e.Summary)
                .ToListAsync();
            Assert.Equal(["Third.", "Second.", "First."], newestFirst);

            var since = await context.AuditEvents
                .CountAsync(e => e.OccurredAt >= midday.AddMinutes(30));
            Assert.Equal(2, since);
        });
    }

    [Fact]
    public async Task Typed_identifiers_round_trip_through_the_database()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var group = new AssetGroup("Plant room");
        var asset = new Asset("PR-001", "Boiler house", group.Id);

        await database.WithContextAsync(async context =>
        {
            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            await context.SaveChangesAsync();
        });

        await database.WithContextAsync(async context =>
        {
            var loaded = await context.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal(asset.Id, loaded.Id);
            Assert.Equal(group.Id, loaded.AssetGroupId);
        });
    }
}
