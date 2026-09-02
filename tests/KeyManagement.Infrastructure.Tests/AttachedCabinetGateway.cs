using KeyManagement.Application.Abstractions;
using KeyManagement.Domain;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// A gateway with every cabinet attached.
/// </summary>
/// <remarks>
/// Custody now refuses a release when the cabinet holding the item cannot be reached, which is
/// correct and which would otherwise refuse every test that is not about the device layer.
/// Tests that are about reachability use <see cref="NullCabinetGateway"/> instead.
/// </remarks>
public sealed class AttachedCabinetGateway : ICabinetGateway
{
    /// <summary>Every unlock that was sent, in order.</summary>
    public List<(CabinetId Cabinet, string Position, CorrelationId Correlation)> Unlocks { get; } = [];

    /// <inheritdoc />
    public bool IsAttached(CabinetId cabinetId) => true;

    /// <inheritdoc />
    public Task<bool> UnlockAsync(
        CabinetId cabinetId,
        string position,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        Unlocks.Add((cabinetId, position, correlationId));
        return Task.FromResult(true);
    }
}
