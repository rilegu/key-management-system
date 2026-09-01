using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KeyManagement.Server;

/// <summary>
/// The live event stream clients subscribe to.
/// </summary>
/// <remarks>
/// Authorized like any request. The hub sends and never receives: a client cannot ask it to do
/// anything, so there is no second command surface to secure alongside the API.
/// </remarks>
[Authorize]
public sealed class CustodyHub : Hub
{
    /// <summary>Name of the client method that receives audit records.</summary>
    public const string ActivityMethod = "Activity";

    /// <summary>Where the hub is mapped.</summary>
    public const string Path = "/hubs/events";
}

/// <summary>
/// Publishes over SignalR.
/// </summary>
public sealed class SignalRCustodyEventPublisher : ICustodyEventPublisher
{
    // Source-generated rather than an interpolated LogWarning call: the message template is
    // compiled once instead of formatted on every failure.
    private static readonly Action<ILogger, Exception?> UndeliveredPush =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(UndeliveredPush)),
            "Could not push activity to connected clients.");

    private readonly IHubContext<CustodyHub> _hub;
    private readonly ILogger<SignalRCustodyEventPublisher> _logger;

    /// <summary>Creates the publisher.</summary>
    /// <param name="hub">The hub to send through.</param>
    /// <param name="logger">Records a push that could not be delivered.</param>
    public SignalRCustodyEventPublisher(
        IHubContext<CustodyHub> hub,
        ILogger<SignalRCustodyEventPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        AuditEventSummary activity,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _hub.Clients.All
                .SendAsync(CustodyHub.ActivityMethod, activity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed push must not fail the custody command that produced it. The write is
            // already committed and is the system of record; a client that missed this
            // recovers on its next reload.
            UndeliveredPush(_logger, exception);
        }
    }
}
