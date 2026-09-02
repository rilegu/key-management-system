using System.Net;
using System.Net.Sockets;

namespace KeyManagement.Server.Devices;

/// <summary>
/// Listens for cabinets.
/// </summary>
/// <remarks>
/// <para>
/// Cabinets dial in; the server never dials out. They sit behind the site firewall and the
/// server is the reachable party, so reconnect belongs to the cabinet and noticing a
/// disconnection belongs here.
/// </para>
/// <para>
/// Self-contained on purpose. Nothing outside this folder knows there is a socket, so lifting
/// the whole thing into its own worker process later is a move rather than a rewrite.
/// </para>
/// </remarks>
public sealed class DeviceGatewayService : BackgroundService
{
    private readonly DeviceGatewayOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly CabinetRegistry _registry;
    private readonly ILogger<DeviceGatewayService> _logger;

    /// <summary>Creates the listener.</summary>
    /// <param name="options">Where to listen and how patient to be.</param>
    /// <param name="scopes">Creates a scope per message.</param>
    /// <param name="registry">Where attached cabinets are recorded.</param>
    /// <param name="logger">Records what the gateway is doing.</param>
    public DeviceGatewayService(
        DeviceGatewayOptions options,
        IServiceScopeFactory scopes,
        CabinetRegistry registry,
        ILogger<DeviceGatewayService> logger)
    {
        _options = options;
        _scopes = scopes;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>The port actually bound, which differs from the configured one when it was zero.</summary>
    /// <remarks>Port zero lets a test take whatever the machine has free.</remarks>
    public int BoundPort { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Disabled(_logger, null);
            return;
        }

        var listener = new TcpListener(IPAddress.Parse(_options.BindAddress), _options.Port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Listening(_logger, _options.BindAddress, BoundPort, null);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);

                // Each connection runs on its own. One cabinet misbehaving must not stop the
                // listener from accepting the others.
                _ = ServeAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        // Nagle's algorithm holds small writes back waiting for company. Every frame here is
        // small and every one of them is wanted immediately.
        client.NoDelay = true;

        var session = new CabinetSession(
            client.GetStream(), _scopes, _options, _registry, _logger);

        try
        {
            await using (session.ConfigureAwait(false))
            {
                await session.RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SessionFailed(_logger, exception);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static readonly Action<ILogger, Exception?> Disabled =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(Disabled)),
            "The device gateway is disabled; no cabinet can attach.");

    private static readonly Action<ILogger, string, int, Exception?> Listening =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(2, nameof(Listening)),
            "The device gateway is listening on {Address}:{Port}.");

    private static readonly Action<ILogger, Exception?> SessionFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(SessionFailed)),
            "A cabinet session ended unexpectedly.");
}
