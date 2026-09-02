using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using KeyManagement.Devices.Protocol;

namespace KeyManagement.DeviceSimulator;

/// <summary>
/// One simulated cabinet, for as long as the process runs.
/// </summary>
/// <remarks>
/// <para>
/// Everything that makes the device link worth testing lives here: a monotonic sequence, a
/// buffer that fills while disconnected, reconnect with backoff, and replay from whatever the
/// server says it last applied.
/// </para>
/// <para>
/// It never decides anything about custody. It reports what happened at its positions and does
/// what it is told, which is exactly the amount of authority a device on a building network
/// should have.
/// </para>
/// </remarks>
public sealed class CabinetDevice : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(10);

    private readonly CabinetOptions _options;
    private readonly SimulatorOptions _simulator;
    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2 _authority;
    private readonly ConcurrentDictionary<string, string> _positions = new(StringComparer.Ordinal);

    // Events the server has not acknowledged. A reconnect replays from here, which is what
    // makes a disconnection a delay rather than a hole in the record.
    private readonly List<SlotStateChanged> _unacknowledged = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Random _jitter = new();

    private SslStream? _stream;
    private TcpClient? _client;
    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a cabinet.</summary>
    /// <param name="options">Which cabinet this is.</param>
    /// <param name="simulator">Where the server is and how to authenticate to it.</param>
    /// <param name="certificate">Its own certificate, with the private key.</param>
    /// <param name="authority">What the server's certificate must chain to.</param>
    public CabinetDevice(
        CabinetOptions options,
        SimulatorOptions simulator,
        X509Certificate2 certificate,
        X509Certificate2 authority)
    {
        _options = options;
        _simulator = simulator;
        _certificate = certificate;
        _authority = authority;

        foreach (var position in options.Positions)
        {
            _positions[position.Position] = position.Occupied ? "Occupied" : "Empty";
        }
    }

    /// <summary>The cabinet's name.</summary>
    public string Name => _options.Name;

    /// <summary>How this cabinet is currently misbehaving, if at all.</summary>
    public FaultInjection Faults { get; } = new();

    /// <summary>Whether it currently has a connection to the server.</summary>
    public bool IsAttached => _stream is not null;

    /// <summary>Whether the operator has told it to stay disconnected.</summary>
    public bool IsHeldOffline { get; private set; }

    /// <summary>How many events are waiting to be acknowledged.</summary>
    public int BufferedEvents
    {
        get
        {
            lock (_unacknowledged)
            {
                return _unacknowledged.Count;
            }
        }
    }

    /// <summary>What this cabinet believes about each of its positions.</summary>
    /// <returns>Position labels and their states, in label order.</returns>
    public IReadOnlyList<(string Position, string State)> Positions() =>
        [.. _positions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value))];

    /// <summary>Runs until the process stops, reconnecting whenever the link drops.</summary>
    /// <param name="cancellationToken">Stops the cabinet.</param>
    /// <returns>A task that completes when it is stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsHeldOffline)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await AttachAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
                await ServeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException
                                                  or AuthenticationException or ProtocolException)
            {
                Console.WriteLine($"[{Name}] link lost: {exception.Message}");
            }
            finally
            {
                await CloseAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Backoff with jitter. Without the jitter, a site whose network blinked would have
            // every cabinet reconnect in the same millisecond, repeatedly.
            var backoff = TimeSpan.FromMilliseconds(
                Math.Min(MaximumBackoff.TotalMilliseconds, MinimumBackoff.TotalMilliseconds * Math.Pow(2, attempt++))
                + _jitter.Next(0, 250));

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records that a position changed, and reports it if the link is up.</summary>
    /// <param name="position">Which position.</param>
    /// <param name="state">Occupied, Empty or Faulted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the event has been queued or sent.</returns>
    /// <remarks>
    /// The sequence number is allocated here, whether or not the link is up. Numbering only
    /// what gets through would leave the server unable to tell a lost event from one that never
    /// happened.
    /// </remarks>
    public async Task ReportAsync(string position, string state, CancellationToken cancellationToken = default)
    {
        if (!_positions.ContainsKey(position))
        {
            Console.WriteLine($"[{Name}] no position {position}.");
            return;
        }

        _positions[position] = state;

        var change = new SlotStateChanged(
            Interlocked.Increment(ref _sequence), position, state, DateTimeOffset.UtcNow);

        lock (_unacknowledged)
        {
            _unacknowledged.Add(change);
        }

        if (Faults.ShouldDrop())
        {
            Console.WriteLine($"[{Name}] dropped {position} {state} (sequence {change.Sequence}).");
            return;
        }

        await SendEventAsync(change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Presents a holder's credentials at the keypad.</summary>
    /// <param name="userName">Who they say they are.</param>
    /// <param name="pin">The PIN entered.</param>
    /// <param name="position">The position wanted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the request has been sent.</returns>
    public async Task PresentAsync(
        string userName,
        string pin,
        string position,
        CancellationToken cancellationToken = default)
    {
        if (!IsAttached)
        {
            Console.WriteLine($"[{Name}] not attached; the keypad cannot ask anyone.");
            return;
        }

        await SendAsync(
            MessageType.AccessRequest,
            new AccessRequest(Guid.CreateVersion7(), position, userName, pin),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the link, as though the network had gone.</summary>
    public void Drop()
    {
        IsHeldOffline = true;
        _client?.Close();
    }

    /// <summary>Allows the cabinet to reconnect.</summary>
    public void Attach() => IsHeldOffline = false;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _certificate.Dispose();
    }

    private async Task AttachAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_simulator.Host, _simulator.Port, cancellationToken).ConfigureAwait(false);

        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, ValidateServer);

        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = _simulator.ServerName,
                ClientCertificates = [_certificate],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            cancellationToken).ConfigureAwait(false);

        _client = client;
        _stream = ssl;

        long lastSent;
        lock (_unacknowledged)
        {
            lastSent = _unacknowledged.Count > 0 ? _unacknowledged[^1].Sequence : Interlocked.Read(ref _sequence);
        }

        await SendAsync(
            MessageType.Hello,
            new Hello(Name, _options.FirmwareVersion, ProtocolLimits.Version, lastSent),
            cancellationToken).ConfigureAwait(false);

        var frame = await FrameCodec.ReadAsync(ssl, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("The server closed the connection during the handshake.");

        var ack = FrameCodec.Decode<HelloAck>(frame);

        if (!ack.Accepted)
        {
            throw new ProtocolException($"Refused: {ack.Reason}");
        }

        // Resume numbering above whatever the server already has. A cabinet that restarts and
        // begins again at one would have every event it sends discarded as already applied —
        // the sequence check doing its job, on a cabinet that had forgotten its own history.
        // A real one keeps this in non-volatile memory; this one adopts the server's answer.
        var resumeFrom = Math.Max(Interlocked.Read(ref _sequence), ack.LastAppliedSequence);
        Interlocked.Exchange(ref _sequence, resumeFrom);

        Console.WriteLine(
            $"[{Name}] attached; resuming from sequence {resumeFrom}.");

        await ReplayAsync(ack.LastAppliedSequence, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplayAsync(long lastApplied, CancellationToken cancellationToken)
    {
        List<SlotStateChanged> outstanding;

        lock (_unacknowledged)
        {
            // Anything at or below what the server applied is settled. What is left is the gap
            // the disconnection created, and the server discards any overlap anyway.
            _unacknowledged.RemoveAll(e => e.Sequence <= lastApplied);
            outstanding = [.. _unacknowledged.OrderBy(e => e.Sequence)];
        }

        if (outstanding.Count == 0)
        {
            return;
        }

        Console.WriteLine($"[{Name}] replaying {outstanding.Count} buffered event(s).");

        await SendAsync(MessageType.EventBatch, new EventBatch(outstanding), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not attached.");
        using var heartbeats = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, heartbeats.Token);

        var beating = HeartbeatAsync(linked.Token);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(stream, linked.Token).ConfigureAwait(false);

                if (frame is null)
                {
                    return;
                }

                await HandleAsync(frame.Value, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await heartbeats.CancelAsync().ConfigureAwait(false);
            try
            {
                await beating.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the heartbeat loop stops with the connection.
            }
        }
    }

    private async Task HandleAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case MessageType.UnlockSlot:
                var unlock = FrameCodec.Decode<UnlockSlot>(frame);
                Console.WriteLine($"[{Name}] releasing {unlock.Position} for {unlock.OpenFor.TotalSeconds:F0}s.");

                await SendAsync(
                    MessageType.CommandOutcome,
                    new CommandOutcome(unlock.CorrelationId, true, null, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.AccessResult:
                var result = FrameCodec.Decode<AccessResult>(frame);
                Console.WriteLine($"[{Name}] keypad: {(result.Granted ? "granted" : "refused")} - {result.Message}");
                break;

            case MessageType.RequestSnapshot:
                var request = FrameCodec.Decode<RequestSnapshot>(frame);
                await SendAsync(
                    MessageType.Snapshot,
                    new Snapshot(
                        request.CorrelationId,
                        Interlocked.Read(ref _sequence),
                        [.. Positions().Select(p => new SlotReport(p.Position, p.State, DateTimeOffset.UtcNow))]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.Ping:
                await SendAsync(
                    MessageType.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow), cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                break;
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            await SendAsync(MessageType.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SendEventAsync(SlotStateChanged change, CancellationToken cancellationToken)
    {
        if (!IsAttached)
        {
            Console.WriteLine($"[{Name}] buffered {change.Position} {change.State} (sequence {change.Sequence}).");
            return;
        }

        await SendAsync(MessageType.SlotStateChanged, change, cancellationToken).ConfigureAwait(false);

        if (Faults.Duplicate)
        {
            await SendAsync(MessageType.SlotStateChanged, change, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync<T>(MessageType type, T message, CancellationToken cancellationToken)
    {
        var stream = _stream;

        if (stream is null)
        {
            return;
        }

        await Faults.DelayAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await FrameCodec.WriteAsync(stream, type, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task CloseAsync()
    {
        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        client?.Dispose();
    }

    private bool ValidateServer(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        // Against this deployment's authority, not the machine's trust store. A cabinet that
        // accepts any server presenting a publicly trusted certificate is a cabinet that can be
        // pointed somewhere else.
        using var presented = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        using var built = new X509Chain();
        built.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        built.ChainPolicy.CustomTrustStore.Add(_authority);
        built.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return built.Build(presented);
    }
}
