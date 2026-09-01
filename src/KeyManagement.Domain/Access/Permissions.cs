namespace KeyManagement.Domain.Access;

/// <summary>
/// What a role allows. Flags, because a role holds a set of these and the set is what gets
/// stored and checked.
/// </summary>
/// <remarks>
/// <see cref="CheckoutAsset"/> is necessary but not sufficient on its own: checkout is
/// additionally scoped by asset group, so holding it permits the holder's groups rather than
/// every asset in the building.
/// </remarks>
[Flags]
public enum Permissions
{
    /// <summary>No permissions.</summary>
    None = 0,

    /// <summary>Request and return assets, within the holder's asset groups.</summary>
    CheckoutAsset = 1,

    /// <summary>Create and amend holders, roles and group membership.</summary>
    ManageUsers = 2,

    /// <summary>Acknowledge an active alarm.</summary>
    AcknowledgeAlarm = 4,

    /// <summary>Search and export the audit trail.</summary>
    ViewAudit = 8,
}
