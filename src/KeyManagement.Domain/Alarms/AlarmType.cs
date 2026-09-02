namespace KeyManagement.Domain.Alarms;

/// <summary>
/// What an alarm is about.
/// </summary>
public enum AlarmType
{
    /// <summary>An item is held past its curfew.</summary>
    OverdueItem = 0,

    /// <summary>A cabinet stopped answering, so its positions are no longer confirmed.</summary>
    CabinetOffline = 1,

    /// <summary>A position emptied with no release authorized behind it.</summary>
    UnauthorizedRemoval = 2,

    /// <summary>A cabinet reports a position it cannot read.</summary>
    PositionFault = 3,

    /// <summary>A release nobody collected. The position was opened and nothing came out.</summary>
    UncollectedRelease = 4,
}

/// <summary>
/// How much it matters.
/// </summary>
/// <remarks>
/// Three levels, not five. An operator scanning a list needs to know what to deal with first,
/// and a scale finer than that turns into everything being important.
/// </remarks>
public enum AlarmSeverity
{
    /// <summary>Worth knowing. Nothing is wrong.</summary>
    Information = 0,

    /// <summary>Something needs attention, but custody is still accounted for.</summary>
    Warning = 1,

    /// <summary>Custody is not accounted for, or something happened that nobody authorized.</summary>
    Critical = 2,
}

/// <summary>
/// Whether anyone has dealt with it.
/// </summary>
public enum AlarmStatus
{
    /// <summary>Raised and not yet acknowledged.</summary>
    Active = 0,

    /// <summary>Someone has seen it and said so.</summary>
    Acknowledged = 1,
}
