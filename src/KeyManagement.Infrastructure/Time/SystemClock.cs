using KeyManagement.Application.Abstractions;

namespace KeyManagement.Infrastructure.Time;

/// <summary>
/// The real clock. The only place in the system that reads the machine's time.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
