using KeyManagement.Domain;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Sends commands to attached cabinets.
/// </summary>
/// <remarks>
/// A port, so a use case never names TCP or the wire format. The implementation lives with the
/// gateway; the custody rules only need to know whether a cabinet can be reached and whether it
/// did what it was told.
/// </remarks>
public interface ICabinetGateway
{
    /// <summary>Whether a cabinet currently has a working connection.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <returns><see langword="true"/> when a command would reach it.</returns>
    bool IsAttached(CabinetId cabinetId);

    /// <summary>Instructs a cabinet to release a position.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="position">The position to release.</param>
    /// <param name="correlationId">Ties the command to the request that caused it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the command was handed to the cabinet.</returns>
    /// <remarks>
    /// Returning true means the instruction was sent, not that the item was taken. Only the
    /// cabinet's own report of the position changing establishes that.
    /// </remarks>
    Task<bool> UnlockAsync(
        CabinetId cabinetId,
        string position,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A gateway with nothing attached to it.
/// </summary>
/// <remarks>
/// The default when no device layer is hosted, so tests and any future host without a listener
/// compose unchanged. It reports every cabinet as unreachable, which is the truth.
/// </remarks>
public sealed class NullCabinetGateway : ICabinetGateway
{
    /// <inheritdoc />
    public bool IsAttached(CabinetId cabinetId) => false;

    /// <inheritdoc />
    public Task<bool> UnlockAsync(
        CabinetId cabinetId,
        string position,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
