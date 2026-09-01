using KeyManagement.Application.Abstractions;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
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
    private IQueryable<User> Query() =>
        _context.Users.Include(u => u.Roles).Include(u => u.GroupMemberships);
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
