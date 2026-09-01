using KeyManagement.Contracts;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Discards pushes. The default when no transport is configured.
/// </summary>
/// <remarks>
/// The use cases do not require anyone to be listening, so a host without a hub - a test, a
/// future background worker, a command-line tool - composes and runs unchanged. The database
/// remains the system of record either way.
/// </remarks>
public sealed class NullCustodyEventPublisher : ICustodyEventPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(
        AuditEventSummary activity,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
