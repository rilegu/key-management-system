namespace KeyManagement.Domain.Assets;

/// <summary>
/// Where an asset is, as far as the system of record can tell.
/// </summary>
/// <remarks>
/// The two uncertain states are deliberate. An audit trail that reads as confident and is
/// wrong is worse than one that admits a gap, so nothing here optimistically resolves to
/// <see cref="Available"/> without the cabinet confirming it.
/// </remarks>
public enum AssetCustodyState
{
    /// <summary>In its slot and available to a holder who is authorized for it.</summary>
    Available = 0,

    /// <summary>Authorized and the cabinet has been told to release it, not yet confirmed gone.</summary>
    CheckoutPending = 1,

    /// <summary>Confirmed removed and in a holder's possession.</summary>
    CheckedOut = 2,

    /// <summary>A return is authorized, awaiting confirmation the asset is back in its slot.</summary>
    ReturnPending = 3,

    /// <summary>The cabinet reports a fault for its slot; the asset's whereabouts are not trusted.</summary>
    Faulted = 4,

    /// <summary>The server cannot establish where it is, typically an offline cabinet or a lost event.</summary>
    Unknown = 5,
}
