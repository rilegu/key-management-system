namespace KeyManagement.Domain.Cabinets;

/// <summary>
/// Whether the server currently has a working link to a cabinet.
/// </summary>
public enum CabinetStatus
{
    /// <summary>Enrolled but has never connected.</summary>
    NeverConnected = 0,

    /// <summary>Connected and heartbeating.</summary>
    Online = 1,

    /// <summary>Missed its heartbeat allowance. Its slots become unknown, not available.</summary>
    Offline = 2,
}
