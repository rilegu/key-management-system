using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Application.Abstractions;

// The write side. Interfaces here, EF implementations in Infrastructure, so use cases never
// name a database. Reads that only feed a screen go through ICustodyQueries instead, which
// projects straight to contracts rather than loading entities to throw most of them away.

/// <summary>Loads and stores holders.</summary>
public interface IUserRepository
{
    /// <summary>Finds a holder by sign-in name, with roles and group grants loaded.</summary>
    /// <param name="userName">The sign-in name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The holder, or <see langword="null"/> if there is none.</returns>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>Finds a holder by identifier, with roles and group grants loaded.</summary>
    /// <param name="id">The holder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The holder, or <see langword="null"/> if there is none.</returns>
    Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default);
}

/// <summary>Loads and stores assets.</summary>
public interface IAssetRepository
{
    /// <summary>Finds an asset.</summary>
    /// <param name="id">The asset.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The asset, or <see langword="null"/> if there is none.</returns>
    Task<Asset?> FindByIdAsync(AssetId id, CancellationToken cancellationToken = default);
}

/// <summary>Loads cabinets and the slots that hold assets.</summary>
public interface ICabinetRepository
{
    /// <summary>Finds the slot an asset lives in.</summary>
    /// <param name="assetId">The asset.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The slot, or <see langword="null"/> if the asset is not assigned to one.</returns>
    Task<Slot?> FindSlotHoldingAsync(AssetId assetId, CancellationToken cancellationToken = default);

    /// <summary>Finds a cabinet by the name it was enrolled under.</summary>
    /// <param name="name">The cabinet's name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cabinet, or <see langword="null"/> if no such cabinet is enrolled.</returns>
    /// <remarks>
    /// Cabinets identify themselves by name. A cabinet is enrolled by someone who typed one, and
    /// it has no way to learn a database key.
    /// </remarks>
    Task<Cabinet?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Finds a cabinet and every position it holds.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cabinet, or <see langword="null"/>.</returns>
    Task<Cabinet?> FindWithSlotsAsync(CabinetId cabinetId, CancellationToken cancellationToken = default);

    /// <summary>Finds one position within a cabinet.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="position">The position label.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The position, or <see langword="null"/> if the cabinet has no such position.</returns>
    Task<Slot?> FindSlotAsync(
        CabinetId cabinetId,
        string position,
        CancellationToken cancellationToken = default);
}

/// <summary>Keeps what cabinets actually said, before interpretation.</summary>
/// <remarks>
/// Written for every message, including ones discarded as already seen. When the server and a
/// cabinet disagree about what happened, this is the only record of the cabinet's side.
/// </remarks>
public interface IDeviceEventLog
{
    /// <summary>Records a message as received.</summary>
    /// <param name="deviceEvent">The message.</param>
    void Record(DeviceEvent deviceEvent);
}

/// <summary>Loads and stores custody requests.</summary>
public interface ICheckoutRepository
{
    /// <summary>Records a new custody request, permitted or refused.</summary>
    /// <param name="checkout">The request.</param>
    void Add(Checkout checkout);

    /// <summary>Finds a checkout.</summary>
    /// <param name="id">The checkout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The checkout, or <see langword="null"/> if there is none.</returns>
    Task<Checkout?> FindByIdAsync(CheckoutId id, CancellationToken cancellationToken = default);

    /// <summary>Finds the unsettled request for an asset, if it has one.</summary>
    /// <param name="assetId">The asset.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open checkout, or <see langword="null"/>.</returns>
    /// <remarks>
    /// An asset has at most one, which is what makes "who has this" answerable without
    /// picking between candidates.
    /// </remarks>
    Task<Checkout?> FindOpenForAssetAsync(AssetId assetId, CancellationToken cancellationToken = default);

    /// <summary>Every checkout that has not settled.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Pending, active and overdue checkouts.</returns>
    /// <remarks>
    /// What the sweep walks. Bounded by how much is out at once rather than by how much has
    /// ever happened, so it stays small however long the system has been running.
    /// </remarks>
    Task<IReadOnlyList<Checkout>> ListOpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Issues, finds and revokes refresh tokens.</summary>
public interface IRefreshTokenStore
{
    /// <summary>Records an issued token.</summary>
    /// <param name="token">The token record, holding only the hash.</param>
    void Add(RefreshToken token);

    /// <summary>Finds a token by its hash.</summary>
    /// <param name="tokenHash">Hash of the presented token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The record, or <see langword="null"/> if no such token was issued.</returns>
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);
}

/// <summary>Appends to the audit trail.</summary>
/// <remarks>
/// Append only, by shape. There is no method here to amend or remove a record, and
/// <c>AppendOnlyAuditInterceptor</c> refuses it at the database if one is ever reached another
/// way.
/// </remarks>
public interface IAuditTrail
{
    /// <summary>Adds a record. It is written when the surrounding work is saved.</summary>
    /// <param name="auditEvent">The record.</param>
    void Record(AuditEvent auditEvent);
}

/// <summary>Commits a unit of work, optionally as one transaction.</summary>
public interface IUnitOfWork
{
    /// <summary>Writes everything pending.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows changed.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs work inside one transaction, so a custody change and the audit record explaining
    /// it stand or fall together.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whatever the work returned.</returns>
    Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}
