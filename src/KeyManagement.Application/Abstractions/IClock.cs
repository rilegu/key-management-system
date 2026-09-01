namespace KeyManagement.Application.Abstractions;

/// <summary>
/// The current time, injected rather than read from <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
/// <remarks>
/// Overdue detection and token expiry are time-dependent rules, and a rule that reads the
/// clock directly can only be tested by waiting. Everything here is UTC; local time is a
/// display concern at the UI boundary.
/// </remarks>
public interface IClock
{
    /// <summary>The current moment, UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
