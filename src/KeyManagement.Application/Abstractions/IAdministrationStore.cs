using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// The things an administrator creates and amends.
/// </summary>
/// <remarks>
/// Deliberately has no delete. Nothing here is removed: a holder is suspended and an item is
/// left in place, so every audit record keeps a subject it can name.
/// </remarks>
public interface IAdministrationStore
{
    /// <summary>Records a new holder.</summary>
    /// <param name="user">The holder.</param>
    void Add(User user);

    /// <summary>Records a new item group.</summary>
    /// <param name="group">The group.</param>
    void Add(AssetGroup group);

    /// <summary>Records a new item.</summary>
    /// <param name="asset">The item.</param>
    void Add(Asset asset);

    /// <summary>Finds a role.</summary>
    /// <param name="id">The role.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The role, or <see langword="null"/>.</returns>
    Task<Role?> FindRoleAsync(RoleId id, CancellationToken cancellationToken = default);

    /// <summary>Finds an item group.</summary>
    /// <param name="id">The group.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The group, or <see langword="null"/>.</returns>
    Task<AssetGroup?> FindGroupAsync(AssetGroupId id, CancellationToken cancellationToken = default);
}
