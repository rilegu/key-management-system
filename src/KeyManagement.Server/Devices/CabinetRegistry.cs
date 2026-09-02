using System.Collections.Concurrent;
using KeyManagement.Application.Abstractions;
using KeyManagement.Devices.Protocol;
using KeyManagement.Domain;

namespace KeyManagement.Server.Devices;

/// <summary>
/// Which cabinets are attached right now, and how to reach them.
/// </summary>
/// <remarks>
/// A singleton: connections outlive any one request, and a checkout arriving on an HTTP thread
/// has to find the cabinet's socket. Nothing here is persisted — attachment is a fact about
/// this moment, and the database records what the cabinet reported rather than whether it is
/// currently plugged in.
/// </remarks>
public sealed class CabinetRegistry : ICabinetGateway
{
    /// <summary>How long a released position stays open before it locks again.</summary>
    public static readonly TimeSpan UnlockWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<CabinetId, CabinetSession> _attached = new();
    private readonly ILogger<CabinetRegistry> _logger;

    /// <summary>Creates the registry.</summary>
    /// <param name="logger">Records commands that could not be delivered.</param>
    public CabinetRegistry(ILogger<CabinetRegistry> logger) => _logger = logger;

    /// <summary>How many cabinets are attached.</summary>
    public int AttachedCount => _attached.Count;

    /// <inheritdoc />
    public bool IsAttached(CabinetId cabinetId) => _attached.ContainsKey(cabinetId);

    /// <inheritdoc />
    public async Task<bool> UnlockAsync(
        CabinetId cabinetId,
        string position,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!_attached.TryGetValue(cabinetId, out var session))
        {
            return false;
        }

        try
        {
            await session
                .SendAsync(
                    MessageType.UnlockSlot,
                    new UnlockSlot(correlationId.Value, position, UnlockWindow),
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or ProtocolException)
        {
            // The cabinet dropped between the decision and the command. The checkout stays
            // pending, which is correct: nothing has been released, and the cabinet will not
            // report a change that did not happen.
            Undelivered(_logger, position, exception);
            return false;
        }
    }

    /// <summary>Records a cabinet as attached, replacing any earlier connection.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="session">Its connection.</param>
    /// <returns>The connection it replaced, if a stale one was still registered.</returns>
    /// <remarks>
    /// A cabinet that reconnects before the server noticed the old socket had died would
    /// otherwise leave two entries, and commands would go to the dead one.
    /// </remarks>
    public CabinetSession? Attach(CabinetId cabinetId, CabinetSession session)
    {
        CabinetSession? replaced = null;

        _attached.AddOrUpdate(
            cabinetId,
            session,
            (_, existing) =>
            {
                replaced = existing;
                return session;
            });

        return replaced;
    }

    /// <summary>Removes a cabinet, if the connection given is still the registered one.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="session">The connection that is ending.</param>
    public void Detach(CabinetId cabinetId, CabinetSession session) =>
        _attached.TryRemove(new KeyValuePair<CabinetId, CabinetSession>(cabinetId, session));

    private static readonly Action<ILogger, string, Exception?> Undelivered =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(Undelivered)),
            "Could not deliver an unlock for position {Position}; the cabinet dropped.");
}
