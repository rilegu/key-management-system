namespace KeyManagement.Domain.Access;

/// <summary>
/// Grants one holder checkout access to one asset group.
/// </summary>
/// <remarks>
/// A join row with an identity of its own, rather than a bare pair, so that granting and
/// withdrawing access are individually auditable events.
/// </remarks>
public sealed class AssetGroupMembership
{
    private AssetGroupMembership()
    {
    }

    /// <summary>Grants access.</summary>
    /// <param name="userId">The holder.</param>
    /// <param name="assetGroupId">The group they may check out from.</param>
    public AssetGroupMembership(UserId userId, AssetGroupId assetGroupId)
    {
        UserId = userId;
        AssetGroupId = assetGroupId;
    }

    /// <summary>The holder granted access.</summary>
    public UserId UserId { get; private set; }

    /// <summary>The group they may check out from.</summary>
    public AssetGroupId AssetGroupId { get; private set; }
}
