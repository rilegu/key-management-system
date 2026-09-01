namespace KeyManagement.Domain.Access;

/// <summary>
/// Whether a holder may use the system at all, checked before any permission is considered.
/// </summary>
public enum UserStatus
{
    /// <summary>May sign in and hold assets.</summary>
    Active = 0,

    /// <summary>Temporarily barred. Existing checkouts stand and still need returning.</summary>
    Suspended = 1,

    /// <summary>Permanently barred. Retained rather than deleted so the audit trail keeps its subject.</summary>
    Disabled = 2,
}
