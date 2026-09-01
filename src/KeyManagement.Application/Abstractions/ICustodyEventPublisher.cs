using KeyManagement.Contracts;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Pushes what just happened to connected clients.
/// </summary>
/// <remarks>
/// A port rather than a direct dependency on the transport, so a use case never names SignalR.
/// Publishing is best-effort and must never fail the command that produced it: the database is
/// the system of record, and a client that missed a push recovers by reloading.
/// </remarks>
public interface ICustodyEventPublisher
{
    /// <summary>Announces an audit record to everyone entitled to see it.</summary>
    /// <param name="activity">What happened.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the push has been handed off.</returns>
    Task PublishAsync(AuditEventSummary activity, CancellationToken cancellationToken = default);
}
