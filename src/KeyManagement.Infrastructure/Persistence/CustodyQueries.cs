using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// Read-side projections straight to contracts.
/// </summary>
/// <remarks>
/// <para>
/// Every query is <c>AsNoTracking</c> and projects in the database rather than loading entities
/// and mapping them afterwards. These feed screens; nothing they return is used to decide
/// anything.
/// </para>
/// <para>
/// Filtering and ordering always happen on the entity query, with the projection applied last.
/// The other way round asks the provider to order by a property of a constructed object, which
/// it cannot translate and only discovers when the query runs.
/// </para>
/// </remarks>
public sealed class CustodyQueries : ICustodyQueries
{
    /// <summary>The most audit records one search will return.</summary>
    /// <remarks>The trail only grows, so an unbounded query is one that eventually hangs.</remarks>
    public const int MaximumAuditResults = 500;

    private const int RecentEventsOnDashboard = 20;

    private static readonly CheckoutState[] OpenStates =
        [CheckoutState.Pending, CheckoutState.Active, CheckoutState.Overdue];

    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the queries.</summary>
    /// <param name="context">The database.</param>
    public CustodyQueries(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetSummary>> ListAssetsAsync(
        AssetGroupId? assetGroupId = null,
        CancellationToken cancellationToken = default)
    {
        var assets = _context.Assets.AsNoTracking();

        if (assetGroupId is { } group)
        {
            assets = assets.Where(a => a.AssetGroupId == group);
        }

        return await Project(assets.OrderBy(a => a.Reference))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CabinetSummary>> ListCabinetsAsync(
        CancellationToken cancellationToken = default) =>
        await Project(_context.Cabinets.AsNoTracking().OrderBy(c => c.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CabinetSnapshot?> GetCabinetSnapshotAsync(
        CabinetId cabinetId,
        CancellationToken cancellationToken = default)
    {
        var cabinet = await Project(_context.Cabinets.AsNoTracking().Where(c => c.Id == cabinetId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cabinet is null)
        {
            return null;
        }

        var slots = await _context.Slots
            .AsNoTracking()
            .Where(s => s.CabinetId == cabinetId)
            .OrderBy(s => s.Position)
            .Select(s => new SlotSummary(
                s.Id.Value,
                s.Position,
                s.State.ToString(),
                s.LastReportedAt,
                s.AssetId == null ? null : s.AssetId.Value.Value,
                _context.Assets
                    .Where(a => a.Id == s.AssetId)
                    .Select(a => a.Reference)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CabinetSnapshot(cabinet, slots);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckoutSummary>> ListOpenCheckoutsAsync(
        CancellationToken cancellationToken = default) =>
        await Project(_context.Checkouts
                .AsNoTracking()
                .Where(c => OpenStates.Contains(c.State))
                .OrderBy(c => c.DueAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<CheckoutSummary?> GetCheckoutAsync(
        CheckoutId checkoutId,
        CancellationToken cancellationToken = default) =>
        Project(_context.Checkouts.AsNoTracking().Where(c => c.Id == checkoutId))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventSummary>> SearchAuditAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = _context.AuditEvents.AsNoTracking();

        if (query.From is { } from)
        {
            records = records.Where(e => e.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            records = records.Where(e => e.OccurredAt <= to);
        }

        if (query.UserId is { } userId)
        {
            var typed = new UserId(userId);
            records = records.Where(e => e.UserId == typed);
        }

        if (query.AssetId is { } assetId)
        {
            var typed = new AssetId(assetId);
            records = records.Where(e => e.AssetId == typed);
        }

        if (!string.IsNullOrWhiteSpace(query.Type)
            && Enum.TryParse<AuditEventType>(query.Type, ignoreCase: true, out var type))
        {
            records = records.Where(e => e.Type == type);
        }

        var take = Math.Clamp(query.Take, 1, MaximumAuditResults);

        return await records
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .Select(e => new AuditEventSummary(
                e.Id.Value,
                e.Type.ToString(),
                e.OccurredAt,
                e.CorrelationId.Value,
                e.Summary,
                e.UserId == null ? null : e.UserId.Value.Value,
                e.AssetId == null ? null : e.AssetId.Value.Value,
                e.CabinetId == null ? null : e.CabinetId.Value.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var cabinets = await ListCabinetsAsync(cancellationToken).ConfigureAwait(false);
        var open = await ListOpenCheckoutsAsync(cancellationToken).ConfigureAwait(false);

        var uncertain = await Project(_context.Assets
                .AsNoTracking()
                .Where(a => a.CustodyState == AssetCustodyState.Faulted
                         || a.CustodyState == AssetCustodyState.Unknown)
                .OrderBy(a => a.Reference))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recent = await SearchAuditAsync(
                new AuditQuery(Take: RecentEventsOnDashboard), cancellationToken)
            .ConfigureAwait(false);

        return new DashboardSummary(cabinets, open, uncertain, recent);
    }

    private IQueryable<AssetSummary> Project(IQueryable<Asset> assets) =>
        assets.Select(a => new AssetSummary(
            a.Id.Value,
            a.Reference,
            a.Description,
            a.AssetGroupId.Value,
            _context.AssetGroups
                .Where(g => g.Id == a.AssetGroupId)
                .Select(g => g.Name)
                .FirstOrDefault()!,
            a.CustodyState.ToString(),

            // An asset is in at most one slot, guaranteed by the filtered unique index, so
            // these subqueries return one row or none.
            _context.Slots
                .Where(s => s.AssetId == a.Id)
                .Join(_context.Cabinets, s => s.CabinetId, c => c.Id, (s, c) => c.Name)
                .FirstOrDefault(),
            _context.Slots
                .Where(s => s.AssetId == a.Id)
                .Select(s => s.Position)
                .FirstOrDefault()));

    private IQueryable<CabinetSummary> Project(IQueryable<Cabinet> cabinets) =>
        cabinets.Select(c => new CabinetSummary(
            c.Id.Value,
            c.Name,
            c.Site,
            c.Status.ToString(),
            c.LastSeenAt,
            c.FirmwareVersion,
            _context.Slots.Count(s => s.CabinetId == c.Id)));

    private IQueryable<CheckoutSummary> Project(IQueryable<Checkout> checkouts) =>
        checkouts.Select(c => new CheckoutSummary(
            c.Id.Value,
            c.AssetId.Value,
            _context.Assets
                .Where(a => a.Id == c.AssetId)
                .Select(a => a.Reference)
                .FirstOrDefault()!,
            c.UserId.Value,
            _context.Users
                .Where(u => u.Id == c.UserId)
                .Select(u => u.DisplayName)
                .FirstOrDefault()!,
            c.State.ToString(),
            c.RequestedAt,
            c.TakenAt,
            c.DueAt,
            c.ReturnedAt,
            c.DenialReason));
}
