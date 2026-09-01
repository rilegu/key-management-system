namespace KeyManagement.Domain.Access;

/// <summary>
/// Someone who may hold assets. Retained rather than deleted once created, so audit records
/// keep a subject.
/// </summary>
public sealed class User
{
    private readonly List<Role> _roles = [];
    private readonly List<AssetGroupMembership> _groupMemberships = [];

    private User()
    {
        DisplayName = string.Empty;
        UserName = string.Empty;
        PasswordHash = string.Empty;
    }

    /// <summary>Creates an active holder.</summary>
    /// <param name="userName">Unique sign-in name.</param>
    /// <param name="displayName">Name shown in the interface and in audit records.</param>
    /// <param name="passwordHash">Already hashed. This type never sees a plaintext secret.</param>
    public User(string userName, string displayName, string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        Id = UserId.New();
        UserName = userName;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Status = UserStatus.Active;
    }

    /// <summary>Identifies this holder.</summary>
    public UserId Id { get; private set; }

    /// <summary>Unique across all holders.</summary>
    public string UserName { get; private set; }

    /// <summary>Shown in the interface and in audit records.</summary>
    public string DisplayName { get; private set; }

    /// <summary>PBKDF2 hash. Never a plaintext password, and never logged.</summary>
    public string PasswordHash { get; private set; }

    /// <summary>Hash of the PIN entered at a cabinet, when one is set.</summary>
    public string? PinHash { get; private set; }

    /// <summary>Whether the holder may use the system at all.</summary>
    public UserStatus Status { get; private set; }

    /// <summary>Roles this holder has been granted.</summary>
    public IReadOnlyCollection<Role> Roles => _roles;

    /// <summary>Asset groups this holder may check out from.</summary>
    public IReadOnlyCollection<AssetGroupMembership> GroupMemberships => _groupMemberships;

    /// <summary>The union of every permission from every role held.</summary>
    public Permissions EffectivePermissions =>
        _roles.Aggregate(Permissions.None, (all, role) => all | role.Permissions);

    /// <summary>Whether the holder has a permission and is currently allowed to use it.</summary>
    /// <param name="permission">The permission to test.</param>
    /// <returns><see langword="true"/> when the holder is active and holds it.</returns>
    /// <remarks>
    /// Status is checked here rather than by the caller. A suspended holder keeps their roles
    /// so that reinstating them is one change, which makes "has the role" and "may act" two
    /// different questions that are easy to confuse at a call site.
    /// </remarks>
    public bool Can(Permissions permission) =>
        Status == UserStatus.Active && EffectivePermissions.HasFlag(permission);

    /// <summary>Whether the holder may check out assets in a group.</summary>
    /// <param name="group">The group the asset belongs to.</param>
    /// <returns><see langword="true"/> when both the permission and the group membership are present.</returns>
    public bool CanCheckOutFrom(AssetGroupId group) =>
        Can(Permissions.CheckoutAsset) && _groupMemberships.Any(m => m.AssetGroupId == group);

    /// <summary>Grants a role.</summary>
    /// <param name="role">The role to grant.</param>
    public void Grant(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!_roles.Any(r => r.Id == role.Id))
        {
            _roles.Add(role);
        }
    }

    /// <summary>Revokes a role.</summary>
    /// <param name="role">The role to revoke.</param>
    public void Revoke(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        _roles.RemoveAll(r => r.Id == role.Id);
    }

    /// <summary>Permits checkout from an asset group.</summary>
    /// <param name="group">The group to grant.</param>
    public void GrantGroup(AssetGroupId group)
    {
        if (!_groupMemberships.Any(m => m.AssetGroupId == group))
        {
            _groupMemberships.Add(new AssetGroupMembership(Id, group));
        }
    }

    /// <summary>Withdraws checkout access to an asset group.</summary>
    /// <param name="group">The group to revoke.</param>
    public void RevokeGroup(AssetGroupId group) =>
        _groupMemberships.RemoveAll(m => m.AssetGroupId == group);

    /// <summary>Replaces the password hash.</summary>
    /// <param name="passwordHash">Already hashed.</param>
    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>Sets or clears the cabinet PIN hash.</summary>
    /// <param name="pinHash">Already hashed, or <see langword="null"/> to clear.</param>
    public void SetPinHash(string? pinHash) => PinHash = pinHash;

    /// <summary>Changes whether the holder may use the system.</summary>
    /// <param name="status">The new status.</param>
    public void SetStatus(UserStatus status) => Status = status;
}
