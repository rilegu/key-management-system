namespace KeyManagement.Server.Devices;

/// <summary>
/// How the device gateway listens and how patient it is.
/// </summary>
public sealed class DeviceGatewayOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "DeviceGateway";

    /// <summary>Whether to listen at all.</summary>
    /// <remarks>
    /// Off by default. A deployment with no cabinets, and every integration test that is not
    /// about the device layer, should not open a port.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>The port cabinets dial.</summary>
    public int Port { get; set; } = 5610;

    /// <summary>The address to bind. Loopback unless a deployment says otherwise.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>How often a cabinet is expected to report in.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many heartbeats may be missed before a cabinet is treated as gone.
    /// </summary>
    /// <remarks>
    /// Three, not one. A single missed heartbeat is a busy network; three in a row is a cabinet
    /// that is not there, and marking one offline turns every position it holds into an unknown.
    /// </remarks>
    public int MissedHeartbeatsBeforeOffline { get; set; } = 3;

    /// <summary>How long a cabinet has to complete its handshake before the socket is dropped.</summary>
    /// <remarks>
    /// An unauthenticated connection must not be able to sit and hold resources indefinitely.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a cabinet may be silent before it is judged offline.</summary>
    public TimeSpan SilenceBeforeOffline =>
        HeartbeatInterval * MissedHeartbeatsBeforeOffline;
}
