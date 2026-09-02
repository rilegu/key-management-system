using System.Text.Json;
using KeyManagement.Application.Devices;
using KeyManagement.Devices.Protocol;
using KeyManagement.Domain;
using KeyManagement.Domain.Cabinets;

namespace KeyManagement.Server.Devices;

/// <summary>
/// One attached cabinet, for as long as its connection lasts.
/// </summary>
/// <remarks>
/// <para>
/// Reads frames until the cabinet goes away, and translates them into calls on
/// <see cref="CabinetEventService"/>. All the custody reasoning lives there; this type is the
/// wire, the handshake and the timeout.
/// </para>
/// <para>
/// Writes are serialised through a lock because a command can arrive from an HTTP thread while
/// the read loop is running. Two frames interleaved on one socket is unrecoverable framing
/// damage, not a race that resolves itself.
/// </para>
/// </remarks>
public sealed class CabinetSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IServiceScopeFactory _scopes;
    private readonly DeviceGatewayOptions _options;
    private readonly CabinetRegistry _registry;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Creates a session over an accepted connection.</summary>
    /// <param name="stream">The connection. Wrapping it in TLS later changes nothing here.</param>
    /// <param name="scopes">Creates a scope per message, the way a request does.</param>
    /// <param name="options">Timings.</param>
    /// <param name="registry">Where this session registers itself once identified.</param>
    /// <param name="logger">Records what the cabinet did.</param>
    public CabinetSession(
        Stream stream,
        IServiceScopeFactory scopes,
        DeviceGatewayOptions options,
        CabinetRegistry registry,
        ILogger logger)
    {
        _stream = stream;
        _scopes = scopes;
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Which cabinet this is, once the handshake has succeeded.</summary>
    public CabinetId CabinetId { get; private set; }

    /// <summary>Whether the handshake succeeded.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>Sends a frame.</summary>
    /// <typeparam name="T">The message.</typeparam>
    /// <param name="type">What the frame carries.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the frame is on the wire.</returns>
    public async Task SendAsync<T>(MessageType type, T message, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await FrameCodec.WriteAsync(_stream, type, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Runs the session until the cabinet disconnects or falls silent.</summary>
    /// <param name="cancellationToken">Stops the session when the host shuts down.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await HandshakeAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await ReadUntilSilentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            // Framing is broken. There is no way to find where the next frame starts, so the
            // connection ends and the cabinet reconnects.
            ProtocolFault(_logger, exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // An ordinary disconnect, or the host shutting down.
        }
        finally
        {
            if (IsAttached)
            {
                _registry.Detach(CabinetId, this);
                await MarkOfflineAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> HandshakeAsync(CancellationToken cancellationToken)
    {
        // Bounded: an unauthenticated connection must not be able to hold a socket open by
        // simply never speaking.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.HandshakeTimeout);

        var frame = await FrameCodec.ReadAsync(_stream, handshake.Token).ConfigureAwait(false);

        if (frame is not { Type: MessageType.Hello })
        {
            throw new ProtocolException("A cabinet must say Hello before anything else.");
        }

        var hello = FrameCodec.Decode<Hello>(frame.Value);

        await using var scope = _scopes.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<CabinetEventService>();

        var attachment = await events
            .AttachAsync(hello.CabinetName, hello.Credential, hello.FirmwareVersion, hello.ProtocolVersion, cancellationToken)
            .ConfigureAwait(false);

        if (!attachment.Accepted)
        {
            Refused(_logger, hello.CabinetName, attachment.Reason ?? "refused", null);

            await SendAsync(
                MessageType.HelloAck,
                new HelloAck(false, Guid.Empty, 0, attachment.Reason),
                cancellationToken).ConfigureAwait(false);

            return false;
        }

        CabinetId = attachment.CabinetId;
        IsAttached = true;

        var replaced = _registry.Attach(CabinetId, this);
        if (replaced is not null)
        {
            await replaced.DisposeAsync().ConfigureAwait(false);
        }

        await SendAsync(
            MessageType.HelloAck,
            new HelloAck(true, attachment.SessionId, attachment.LastAppliedSequence, null),
            cancellationToken).ConfigureAwait(false);

        Attached(_logger, hello.CabinetName, attachment.LastAppliedSequence, null);
        return true;
    }

    private async Task ReadUntilSilentAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            silence.CancelAfter(_options.SilenceBeforeOffline);

            ProtocolFrame? frame;

            try
            {
                frame = await FrameCodec.ReadAsync(_stream, silence.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Missed its heartbeat allowance. Treated as gone rather than waited on: a
                // cabinet that is not answering cannot be trusted to describe its positions.
                return;
            }

            if (frame is null)
            {
                return;
            }

            await HandleAsync(frame.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case MessageType.Heartbeat:
                // Nothing to do. Having arrived at all is the entire message.
                break;

            case MessageType.SlotStateChanged:
                await ApplyAsync(FrameCodec.Decode<SlotStateChanged>(frame), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MessageType.EventBatch:
                // Buffered while the cabinet was disconnected. Applied oldest first, and the
                // sequence check quietly drops whatever the server already has.
                foreach (var replayed in FrameCodec.Decode<EventBatch>(frame).Events.OrderBy(e => e.Sequence))
                {
                    await ApplyAsync(replayed, cancellationToken).ConfigureAwait(false);
                }

                break;

            case MessageType.Snapshot:
                foreach (var reported in FrameCodec.Decode<Snapshot>(frame).Slots)
                {
                    await ApplyAsync(
                        new SlotStateChanged(0, reported.Position, reported.State, reported.At),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case MessageType.CommandOutcome:
                var result = FrameCodec.Decode<CommandOutcome>(frame);
                if (!result.Success)
                {
                    CommandRefused(_logger, result.CorrelationId, result.Reason ?? "no reason given", null);
                }

                break;

            default:
                // A newer cabinet may send something this build has never heard of. Logging and
                // carrying on beats dropping a connection that is otherwise healthy.
                Unhandled(_logger, frame.Type, null);
                break;
        }
    }

    private async Task ApplyAsync(SlotStateChanged change, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SlotState>(change.State, ignoreCase: true, out var state))
        {
            Unreadable(_logger, change.State, change.Position, null);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<CabinetEventService>();

        var report = new PositionReport(change.Sequence, change.Position, state, change.At);
        var payload = JsonSerializer.Serialize(change, FrameCodec.Json);

        var outcome = await events
            .ApplyReportAsync(CabinetId, report, payload, cancellationToken)
            .ConfigureAwait(false);

        if (outcome == ReportOutcome.Unauthorized)
        {
            Unauthorized(_logger, change.Position, null);
        }
    }

    private async Task MarkOfflineAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CabinetEventService>()
                .MarkOfflineAsync(CabinetId)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The host may already be tearing down. Failing to record the disconnect must not
            // throw out of a finally block.
            OfflineNotRecorded(_logger, exception);
        }
    }

    private static readonly Action<ILogger, string, long, Exception?> Attached =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(1, nameof(Attached)),
            "Cabinet {Cabinet} attached; resuming from sequence {Sequence}.");

    private static readonly Action<ILogger, string, string, Exception?> Refused =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(Refused)),
            "Refused an attachment claiming to be {Cabinet}: {Reason}");

    private static readonly Action<ILogger, string, Exception?> ProtocolFault =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3, nameof(ProtocolFault)),
            "Closing a cabinet connection: {Detail}");

    private static readonly Action<ILogger, MessageType, Exception?> Unhandled =
        LoggerMessage.Define<MessageType>(
            LogLevel.Debug,
            new EventId(4, nameof(Unhandled)),
            "Ignoring an unhandled message type {Type}.");

    private static readonly Action<ILogger, string, string, Exception?> Unreadable =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(5, nameof(Unreadable)),
            "A cabinet reported an unrecognised state {State} for position {Position}.");

    private static readonly Action<ILogger, string, Exception?> Unauthorized =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(6, nameof(Unauthorized)),
            "Position {Position} changed with no release authorized.");

    private static readonly Action<ILogger, Guid, string, Exception?> CommandRefused =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(7, nameof(CommandRefused)),
            "A cabinet refused command {CorrelationId}: {Reason}");

    private static readonly Action<ILogger, Exception?> OfflineNotRecorded =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(8, nameof(OfflineNotRecorded)),
            "Could not record a cabinet going offline.");
}
