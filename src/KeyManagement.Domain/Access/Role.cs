namespace KeyManagement.Domain.Access;

/// <summary>
/// A named set of permissions that holders are granted through.
/// </summary>
public sealed class Role
{
    private Role()
    {
        Name = string.Empty;
    }

    /// <summary>Creates a role.</summary>
    /// <param name="name">Unique, human-readable name.</param>
    /// <param name="permissions">What the role allows.</param>
    public Role(string name, Permissions permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = RoleId.New();
        Name = name;
        Permissions = permissions;
    }

    /// <summary>Identifies this role.</summary>
    public RoleId Id { get; private set; }

    /// <summary>Unique across all roles.</summary>
    public string Name { get; private set; }

    /// <summary>What holders in this role may do.</summary>
    public Permissions Permissions { get; private set; }

    /// <summary>Replaces the permission set.</summary>
    /// <param name="permissions">The new set.</param>
    public void SetPermissions(Permissions permissions) => Permissions = permissions;
}
