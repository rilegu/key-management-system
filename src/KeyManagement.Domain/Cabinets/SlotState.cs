namespace KeyManagement.Domain.Cabinets;

/// <summary>
/// The physical condition of one slot, as last reported by its cabinet.
/// </summary>
public enum SlotState
{
    /// <summary>Holds its assigned asset.</summary>
    Occupied = 0,

    /// <summary>Its asset has been taken.</summary>
    Empty = 1,

    /// <summary>Released and waiting for the asset to be removed.</summary>
    Unlocked = 2,

    /// <summary>The cabinet reports a fault: a sensor disagreement, a lock that did not engage.</summary>
    Faulted = 3,

    /// <summary>Not reported recently enough to be trusted, typically because the cabinet is offline.</summary>
    Unknown = 4,
}
