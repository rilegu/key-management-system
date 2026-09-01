namespace KeyManagement.Domain.Assets;

/// <summary>
/// A set of assets that checkout access is granted over.
/// </summary>
/// <remarks>
/// Authorization is granted per group rather than per asset. Adding a key to the plant-room
/// group grants it to everyone who already services plant rooms, which is the operation the
/// people running the system actually perform.
/// </remarks>
public sealed class AssetGroup
{
    private AssetGroup()
    {
        Name = string.Empty;
    }

    /// <summary>Creates a group.</summary>
    /// <param name="name">Unique, human-readable name.</param>
    /// <param name="description">What belongs in it, for whoever maintains the grouping.</param>
    public AssetGroup(string name, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = AssetGroupId.New();
        Name = name;
        Description = description;
    }

    /// <summary>Identifies this group.</summary>
    public AssetGroupId Id { get; private set; }

    /// <summary>Unique across all groups.</summary>
    public string Name { get; private set; }

    /// <summary>What belongs in it.</summary>
    public string? Description { get; private set; }

    /// <summary>Renames the group and replaces its description.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="description">The new description.</param>
    public void Amend(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
    }
}
