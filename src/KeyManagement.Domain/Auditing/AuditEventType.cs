namespace KeyManagement.Domain.Auditing;

/// <summary>
/// What an audit record describes.
/// </summary>
/// <remarks>
/// Refusals are first-class entries rather than absences. "Nothing happened" and "it was
/// refused" look identical in a trail that only records successes, and the difference is
/// usually the reason someone is reading it.
/// </remarks>
public enum AuditEventType
{
    /// <summary>A holder signed in.</summary>
    SignInSucceeded = 0,

    /// <summary>A sign-in was refused.</summary>
    SignInFailed = 1,

    /// <summary>A holder asked for custody of an asset.</summary>
    CheckoutRequested = 2,

    /// <summary>The request was permitted and the cabinet instructed.</summary>
    CheckoutAuthorized = 3,

    /// <summary>The request was refused.</summary>
    CheckoutDenied = 4,

    /// <summary>The cabinet confirmed the asset was taken.</summary>
    CheckoutCompleted = 5,

    /// <summary>A holder started returning an asset.</summary>
    ReturnRequested = 6,

    /// <summary>The cabinet confirmed the asset is back in its slot.</summary>
    ReturnCompleted = 7,

    /// <summary>An asset's state was resolved after a period of uncertainty.</summary>
    CustodyReconciled = 8,

    /// <summary>A cabinet connected.</summary>
    CabinetOnline = 9,

    /// <summary>A cabinet stopped heartbeating.</summary>
    CabinetOffline = 10,

    /// <summary>A cabinet reported a slot fault.</summary>
    SlotFaulted = 11,

    /// <summary>A cabinet reported a slot change with no authorized command behind it.</summary>
    UnauthorizedSlotChange = 12,

    /// <summary>A holder, role or group membership was created or amended.</summary>
    ConfigurationChanged = 13,
}
