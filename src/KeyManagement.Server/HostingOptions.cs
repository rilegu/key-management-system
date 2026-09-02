namespace KeyManagement.Server;

/// <summary>
/// Which parts of the system this process runs.
/// </summary>
public enum HostingRole
{
    /// <summary>Everything: the API, the live feed, the device gateway and the sweep.</summary>
    All = 0,

    /// <summary>The API and the live feed. Cabinets attach to a separate gateway process.</summary>
    Api = 1,

    /// <summary>The device gateway and the sweep. No API surface beyond a health probe.</summary>
    Gateway = 2,
}

/// <summary>
/// How this process is deployed.
/// </summary>
public sealed class HostingOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Hosting";

    /// <summary>
    /// Which parts to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything, by default. One process is the right answer for a single site: it is simpler
    /// to install, simpler to reason about, and the load does not justify anything else.
    /// </para>
    /// <para>
    /// Splitting them keeps cabinets attached across an API restart, which matters when the API
    /// is updated often and the cabinets are not. It has one cost, recorded in the threat model
    /// and the architecture notes: the live feed is per-process, so device activity picked up by
    /// a separate gateway does not reach clients until they reload.
    /// </para>
    /// </remarks>
    public HostingRole Role { get; set; } = HostingRole.All;

    /// <summary>Whether this process serves the HTTP API and the live feed.</summary>
    public bool RunsApi => Role is HostingRole.All or HostingRole.Api;

    /// <summary>Whether this process listens for cabinets and runs the custody sweep.</summary>
    public bool RunsDevices => Role is HostingRole.All or HostingRole.Gateway;
}
