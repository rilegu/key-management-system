using KeyManagement.Application.Abstractions;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Alarms;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>Loads holders with everything an authorization decision needs.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database.</param>
    public UserRepository(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default) =>
        Query().SingleOrDefaultAsync(u => u.UserName == userName, cancellationToken);

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default) =>
        Query().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    // Roles and group grants are always loaded together, because every authorization check
    // needs both and a lazy load here would be a query per decision.
    //
    // Split, because including two collections in one query multiplies their rows together:
    // a holder with four roles and six groups fetches twenty-four rows to build ten objects.
    private IQueryable<User> Query() =>
        _context.Users
            .Include(u => u.Roles)
            .Include(u => u.GroupMemberships)
            .AsSplitQuery();
}

/// <summary>Loads assets for custody decisions.</summary>
public sealed class AssetRepository : IAssetRepository
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database.</param>
    public AssetRepository(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Asset?> FindByIdAsync(AssetId id, CancellationToken cancellationToken = default) =>
        _context.Assets.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
}

/// <summary>Finds the slot an asset lives in.</summary>
public sealed class CabinetRepository : ICabinetRepository
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database.</param>
    public CabinetRepository(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Slot?> FindSlotHoldingAsync(
        AssetId assetId,
        CancellationToken cancellationToken = default) =>
        _context.Slots.SingleOrDefaultAsync(s => s.AssetId == assetId, cancellationToken);

    /// <inheritdoc />
    public Task<Cabinet?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
        _context.Cabinets.SingleOrDefaultAsync(c => c.Name == name, cancellationToken);

    /// <inheritdoc />
    public Task<Cabinet?> FindWithSlotsAsync(
        CabinetId cabinetId,
        CancellationToken cancellationToken = default) =>
        _context.Cabinets
            .Include(c => c.Slots)
            .SingleOrDefaultAsync(c => c.Id == cabinetId, cancellationToken);

    /// <inheritdoc />
    public Task<Slot?> FindSlotAsync(
        CabinetId cabinetId,
        string position,
        CancellationToken cancellationToken = default) =>
        _context.Slots.SingleOrDefaultAsync(
            s => s.CabinetId == cabinetId && s.Position == position, cancellationToken);
}

/// <summary>Keeps what cabinets reported, as reported.</summary>
public sealed class DeviceEventLog : IDeviceEventLog
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the log.</summary>
    /// <param name="context">The database.</param>
    public DeviceEventLog(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Record(DeviceEvent deviceEvent) => _context.DeviceEvents.Add(deviceEvent);
}

/// <summary>Stores and finds custody requests.</summary>
public sealed class CheckoutRepository : ICheckoutRepository
{
    private static readonly CheckoutState[] OpenStates =
        [CheckoutState.Pending, CheckoutState.Active, CheckoutState.Overdue];

    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database.</param>
    public CheckoutRepository(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Add(Checkout checkout) => _context.Checkouts.Add(checkout);

    /// <inheritdoc />
    public Task<Checkout?> FindByIdAsync(CheckoutId id, CancellationToken cancellationToken = default) =>
        _context.Checkouts.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Checkout>> ListOpenAsync(
        CancellationToken cancellationToken = default) =>
        await _context.Checkouts
            .Where(c => OpenStates.Contains(c.State))
            .OrderBy(c => c.RequestedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Checkout?> FindOpenForAssetAsync(
        AssetId assetId,
        CancellationToken cancellationToken = default) =>
        _context.Checkouts
            .Where(c => c.AssetId == assetId && OpenStates.Contains(c.State))
            .OrderByDescending(c => c.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Stores and finds refresh tokens by hash.</summary>
public sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the store.</summary>
    /// <param name="context">The database.</param>
    public RefreshTokenStore(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Add(RefreshToken token) => _context.RefreshTokens.Add(token);

    /// <inheritdoc />
    public Task<RefreshToken?> FindByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) =>
        _context.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
}

/// <summary>The things an administrator creates and amends.</summary>
public sealed class AdministrationStore : IAdministrationStore
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the store.</summary>
    /// <param name="context">The database.</param>
    public AdministrationStore(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Add(User user) => _context.Users.Add(user);

    /// <inheritdoc />
    public void Add(AssetGroup group) => _context.AssetGroups.Add(group);

    /// <inheritdoc />
    public void Add(Asset asset) => _context.Assets.Add(asset);

    /// <inheritdoc />
    public Task<Role?> FindRoleAsync(RoleId id, CancellationToken cancellationToken = default) =>
        _context.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<AssetGroup?> FindGroupAsync(
        AssetGroupId id,
        CancellationToken cancellationToken = default) =>
        _context.AssetGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
}

/// <summary>Raises and finds alarms.</summary>
public sealed class AlarmRepository : IAlarmRepository
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database.</param>
    public AlarmRepository(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Add(Alarm alarm) => _context.Alarms.Add(alarm);

    /// <inheritdoc />
    public Task<Alarm?> FindByIdAsync(AlarmId id, CancellationToken cancellationToken = default) =>
        _context.Alarms.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsActiveAsync(string scope, CancellationToken cancellationToken = default) =>
        _context.Alarms.AnyAsync(
            a => a.Scope == scope && a.Status == AlarmStatus.Active, cancellationToken);

    /// <inheritdoc />
    public Task<Alarm?> FindActiveAsync(string scope, CancellationToken cancellationToken = default) =>
        _context.Alarms.SingleOrDefaultAsync(
            a => a.Scope == scope && a.Status == AlarmStatus.Active, cancellationToken);
}

/// <summary>Appends to the audit trail.</summary>
public sealed class AuditTrail : IAuditTrail
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the trail.</summary>
    /// <param name="context">The database.</param>
    public AuditTrail(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public void Record(AuditEvent auditEvent) => _context.AuditEvents.Add(auditEvent);
}

/// <summary>Commits work, optionally as one transaction.</summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly KeyManagementDbContext _context;

    /// <summary>Creates the unit of work.</summary>
    /// <param name="context">The database.</param>
    public UnitOfWork(KeyManagementDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Joins an outer transaction rather than starting a nested one, which SQLite does not
        // support. Callers can compose without knowing who began it.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (transaction.ConfigureAwait(false))
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
