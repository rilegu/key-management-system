namespace KeyManagement.Domain.Custody;

/// <summary>
/// The lifecycle of one request to take custody of an asset.
/// </summary>
/// <remarks>
/// Separate from <see cref="Assets.AssetCustodyState"/> because the two answer different
/// questions. A denied request is a fact about the request; the asset it was refused stays
/// exactly where it was.
/// </remarks>
public enum CheckoutState
{
    /// <summary>Authorized and recorded, waiting for the cabinet to confirm the asset was taken.</summary>
    Pending = 0,

    /// <summary>Refused. A recorded outcome, not an error, and terminal.</summary>
    Denied = 1,

    /// <summary>The holder has the asset.</summary>
    Active = 2,

    /// <summary>Still held past its due time.</summary>
    Overdue = 3,

    /// <summary>Back in its slot. Terminal.</summary>
    Returned = 4,

    /// <summary>Authorized, but the asset was never taken: the unlock timed out or the holder walked away. Terminal.</summary>
    Abandoned = 5,
}
