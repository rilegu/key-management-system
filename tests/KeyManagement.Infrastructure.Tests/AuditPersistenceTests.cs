using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// The audit trail's two guarantees: records are never amended, and a custody change is never
/// recorded without one.
/// </summary>
public sealed class AuditPersistenceTests
{
    [Fact]
    public async Task An_audit_record_cannot_be_amended()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var record = new AuditEvent(
                AuditEventType.CheckoutAuthorized,
                DateTimeOffset.UtcNow,
                CorrelationId.New(),
                "Authorized PR-001 to jsmith.");
            context.AuditEvents.Add(record);
            await context.SaveChangesAsync();

            context.Entry(record).State = EntityState.Modified;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task An_audit_record_cannot_be_removed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var record = new AuditEvent(
                AuditEventType.CheckoutDenied,
                DateTimeOffset.UtcNow,
                CorrelationId.New(),
                "Refused PR-001 to jsmith: not in a permitted group.");
            context.AuditEvents.Add(record);
            await context.SaveChangesAsync();

            context.AuditEvents.Remove(record);

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task A_custody_change_and_its_audit_record_stand_or_fall_together()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var correlation = CorrelationId.New();

        AssetId assetId = default;

        await database.WithContextAsync(async context =>
        {
            var group = new AssetGroup("Plant room");
            var asset = new Asset("PR-001", "Boiler house", group.Id);
            assetId = asset.Id;
            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            await context.SaveChangesAsync();
        });

        await database.WithContextAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var asset = await context.Assets.SingleAsync(a => a.Id == assetId);
            asset.BeginCheckout();
            context.AuditEvents.Add(new AuditEvent(
                AuditEventType.CheckoutAuthorized,
                DateTimeOffset.UtcNow,
                correlation,
                "Authorized PR-001.").About(assetId));

            await context.SaveChangesAsync();

            // Something later in the request fails.
            await transaction.RollbackAsync();
        });

        await database.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == assetId);
            var records = await context.AuditEvents
                .CountAsync(e => e.CorrelationId == correlation);

            // Neither survived. The failure mode this rules out is the asset moving while the
            // record of why it moved does not, which leaves a trail that cannot be trusted.
            Assert.Equal(AssetCustodyState.Available, asset.CustodyState);
            Assert.Equal(0, records);
        });
    }

    [Fact]
    public async Task A_correlation_id_gathers_every_record_from_one_command()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var correlation = CorrelationId.New();

        await database.WithContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            context.AuditEvents.AddRange(
                new AuditEvent(AuditEventType.CheckoutRequested, now, correlation, "Requested."),
                new AuditEvent(AuditEventType.CheckoutAuthorized, now, correlation, "Authorized."),
                new AuditEvent(AuditEventType.CheckoutCompleted, now, correlation, "Taken."),
                new AuditEvent(AuditEventType.SignInSucceeded, now, CorrelationId.New(), "Signed in."));
            await context.SaveChangesAsync();

            var story = await context.AuditEvents
                .Where(e => e.CorrelationId == correlation)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync();

            Assert.Equal(3, story.Count);
        });
    }
}
