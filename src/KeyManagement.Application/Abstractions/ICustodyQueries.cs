using KeyManagement.Contracts;
using KeyManagement.Domain;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// The read side: projections straight to contracts, for screens rather than for decisions.
/// </summary>
/// <remarks>
/// Separate from the repositories because the shapes differ. A screen wants an asset with its
/// group name and slot position flattened onto it; a custody decision wants the entity and its
/// invariants. Loading the second to build the first wastes most of what it loaded.
/// </remarks>
public interface ICustodyQueries
{
    /// <summary>Lists assets, newest reference order, optionally limited to one group.</summary>
    /// <param name="assetGroupId">Only assets in this group, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The assets.</returns>
    Task<IReadOnlyList<AssetSummary>> ListAssetsAsync(
        AssetGroupId? assetGroupId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every cabinet.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cabinets.</returns>
    Task<IReadOnlyList<CabinetSummary>> ListCabinetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one cabinet slot by slot.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The snapshot, or <see langword="null"/> if there is no such cabinet.</returns>
    Task<CabinetSnapshot?> GetCabinetSnapshotAsync(
        CabinetId cabinetId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists checkouts that have not settled.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What is currently out.</returns>
    Task<IReadOnlyList<CheckoutSummary>> ListOpenCheckoutsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Reads one checkout.</summary>
    /// <param name="checkoutId">The checkout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The checkout, or <see langword="null"/> if there is none.</returns>
    Task<CheckoutSummary?> GetCheckoutAsync(
        CheckoutId checkoutId,
        CancellationToken cancellationToken = default);

    /// <summary>Searches the audit trail, newest first.</summary>
    /// <param name="query">How to narrow the search.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The matching records.</returns>
    Task<IReadOnlyList<AuditEventSummary>> SearchAuditAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Lists alarms, most recent first.</summary>
    /// <param name="activeOnly">Whether to leave out ones already acknowledged.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The alarms.</returns>
    Task<IReadOnlyList<AlarmSummary>> ListAlarmsAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the audit trail as comma-separated values.
    /// </summary>
    /// <param name="query">How to narrow the export.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The file's contents.</returns>
    /// <remarks>
    /// Produced here rather than in the client, so an export is the same file whoever asked for
    /// it and whatever they asked from.
    /// </remarks>
    Task<string> ExportAuditAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lists holders with what each has been granted.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The holders.</returns>
    Task<IReadOnlyList<HolderSummary>> ListHoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists roles and what they allow.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The roles.</returns>
    Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists item groups.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The groups.</returns>
    Task<IReadOnlyList<AssetGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds the dashboard.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Cabinets, open checkouts, uncertain assets and recent events.</returns>
    Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default);
}
