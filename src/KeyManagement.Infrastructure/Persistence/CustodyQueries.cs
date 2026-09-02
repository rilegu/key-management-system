using System.Buffers;
using System.Globalization;
using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Alarms;
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

    // A value containing any of these has to be quoted, or it silently shifts every later
    // column. Audit summaries are free text, and commas in them are ordinary.
    private static readonly SearchValues<char> SeparatorsNeedingQuotes =
        SearchValues.Create(",\"\r\n");

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
    public async Task<IReadOnlyList<AlarmSummary>> ListAlarmsAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var alarms = _context.Alarms.AsNoTracking();

        if (activeOnly)
        {
            alarms = alarms.Where(a => a.Status == AlarmStatus.Active);
        }

        return await alarms
            .OrderByDescending(a => a.RaisedAt)
            .Take(MaximumAuditResults)
            .Select(a => new AlarmSummary(
                a.Id.Value,
                a.Type.ToString(),
                a.Severity.ToString(),
                a.Status.ToString(),
                a.Summary,
                a.RaisedAt,
                a.CorrelationId.Value,
                a.AssetId == null ? null : a.AssetId.Value.Value,
                _context.Assets
                    .Where(i => a.AssetId != null && i.Id == a.AssetId)
                    .Select(i => i.Reference)
                    .FirstOrDefault(),
                a.CabinetId == null ? null : a.CabinetId.Value.Value,
                a.AcknowledgedAt,
                _context.Users
                    .Where(u => a.AcknowledgedBy != null && u.Id == a.AcknowledgedBy)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ExportAuditAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        var records = await SearchAuditAsync(query, cancellationToken).ConfigureAwait(false);
        var csv = new System.Text.StringBuilder();

        csv.AppendLine("Occurred (UTC),Type,Summary,Correlation,User,Asset,Cabinet");

        foreach (var record in records)
        {
            csv.Append(Escape(record.OccurredAt.ToString("u", CultureInfo.InvariantCulture))).Append(',')
               .Append(Escape(record.Type)).Append(',')
               .Append(Escape(record.Summary)).Append(',')
               .Append(Escape(record.CorrelationId.ToString())).Append(',')
               .Append(Escape(record.UserId?.ToString())).Append(',')
               .Append(Escape(record.AssetId?.ToString())).Append(',')
               .Append(Escape(record.CabinetId?.ToString()))
               .AppendLine();
        }

        return csv.ToString();
    }

    // Quoted whenever the value could break the row, and embedded quotes doubled. An audit
    // summary is free text written by this system, and a comma in one must not silently shift
    // every later column.
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuotes = value.AsSpan().IndexOfAny(SeparatorsNeedingQuotes) >= 0;

        return needsQuotes
            ? string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")
            : value;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HolderSummary>> ListHoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var holders = await _context.Users
            .AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.DisplayName,
                Status = u.Status.ToString(),
                HasPin = u.PinHash != null,
                Roles = u.Roles.Select(r => r.Name).ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Group names are a second query stitched in memory. Reaching AssetGroups from inside the
        // holder projection correlates two tables that EF can only pair with APPLY, which SQLite
        // does not have — and it throws when the screen opens, not when the query is written.
        var memberships = await _context.Set<AssetGroupMembership>()
            .AsNoTracking()
            .Join(
                _context.AssetGroups,
                m => m.AssetGroupId,
                g => g.Id,
                (m, g) => new { m.UserId, g.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var groupsByHolder = memberships
            .GroupBy(m => m.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal)]);

        return
        [
            .. holders.Select(h => new HolderSummary(
                h.Id.Value,
                h.UserName,
                h.DisplayName,
                h.Status,
                h.HasPin,
                h.Roles,
                groupsByHolder.TryGetValue(h.Id, out var names) ? names : [])),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(
        CancellationToken cancellationToken = default)
    {
        var roles = await _context.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.Permissions })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Split apart here rather than in the query: a flags value is one column, and turning
        // it into the names it stands for is not something the provider can translate.
        return
        [
            .. roles.Select(r => new RoleSummary(
                r.Id.Value,
                r.Name,
                [.. Enum.GetValues<Permissions>()
                    .Where(p => p != Permissions.None && r.Permissions.HasFlag(p))
                    .Select(p => p.ToString())])),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetGroupSummary>> ListGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await _context.AssetGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new AssetGroupSummary(
                g.Id.Value,
                g.Name,
                g.Description,
                _context.Assets.Count(a => a.AssetGroupId == g.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
