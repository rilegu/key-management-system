namespace KeyManagement.DeviceSimulator;

/// <summary>
/// The ways this cabinet can misbehave on purpose.
/// </summary>
/// <remarks>
/// The reason the simulator is a separate process rather than a fake inside the tests. A fake
/// fails only in the ways it was written to fail; these settings let a real connection be made
/// slow, lossy and repetitive while the server is none the wiser.
/// </remarks>
public sealed class FaultInjection
{
    private readonly Random _random = new();

    /// <summary>Delay added before each frame is sent.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Percentage of outbound events silently dropped.</summary>
    /// <remarks>
    /// Dropped after being given a sequence number, so the server sees a gap rather than a
    /// renumbered stream. That is what a real loss looks like.
    /// </remarks>
    public int DropPercent { get; set; }

    /// <summary>Whether each event is sent twice.</summary>
    public bool Duplicate { get; set; }

    /// <summary>Whether to hold events back and send them out of order.</summary>
    public bool Reorder { get; set; }

    /// <summary>Whether anything is currently being interfered with.</summary>
    public bool IsClean =>
        Latency == TimeSpan.Zero && DropPercent == 0 && !Duplicate && !Reorder;

    /// <summary>Waits, if latency is configured.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the delay is over.</returns>
    public Task DelayAsync(CancellationToken cancellationToken = default) =>
        Latency == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Latency, cancellationToken);

    /// <summary>Whether this particular event should be dropped.</summary>
    /// <returns><see langword="true"/> when it should not be sent.</returns>
    public bool ShouldDrop() => DropPercent > 0 && _random.Next(100) < DropPercent;

    /// <summary>Describes the current settings for the console.</summary>
    /// <returns>One line.</returns>
    public override string ToString() =>
        IsClean
            ? "clean"
            : $"latency {Latency.TotalMilliseconds:F0}ms, drops {DropPercent}%, " +
              $"duplicate {(Duplicate ? "on" : "off")}, reorder {(Reorder ? "on" : "off")}";
}
