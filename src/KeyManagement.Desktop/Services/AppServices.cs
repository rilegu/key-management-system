using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using KeyManagement.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace KeyManagement.Desktop.Services;

/// <summary>
/// Moves between screens.
/// </summary>
/// <remarks>
/// An interface so a view model can be tested without a window. View models ask to go
/// somewhere; the shell decides what that means.
/// </remarks>
public interface INavigationService
{
    /// <summary>Shows a screen.</summary>
    /// <param name="destination">Where to go.</param>
    void Show(Destination destination);
}

/// <summary>The screens the shell can show.</summary>
public enum Destination
{
    /// <summary>The position board and activity feed.</summary>
    SystemViewer = 0,

    /// <summary>Every item and where it is.</summary>
    Items = 1,

    /// <summary>The activity trail.</summary>
    Activity = 2,

    /// <summary>Things an operator is expected to look at.</summary>
    Alarms = 4,

    /// <summary>Holders, groups and items.</summary>
    Administration = 5,

    /// <summary>Sign-in.</summary>
    SignIn = 3,
}

/// <summary>
/// Tells the operator something without stealing focus.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a confirmation.</summary>
    /// <param name="message">One line.</param>
    void Success(string message);

    /// <summary>Shows a refusal or a problem.</summary>
    /// <param name="message">One line, saying what to do about it where possible.</param>
    void Problem(string message);
}

/// <summary>
/// The live activity feed from the server.
/// </summary>
public interface ILiveActivityFeed : IAsyncDisposable
{
    /// <summary>Raised on the UI thread when the server announces something.</summary>
    event Action<AuditEventSummary>? ActivityReceived;

    /// <summary>Whether the stream is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Connects, using the signed-in holder's token.</summary>
    /// <param name="accessToken">The bearer token to present.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once connected or given up.</returns>
    Task ConnectAsync(string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// The live feed over SignalR.
/// </summary>
/// <remarks>
/// Implements both disposal interfaces on purpose. The container tears itself down
/// synchronously when the window closes, and it refuses to dispose a service that only offers
/// <see cref="IAsyncDisposable"/> — which crashed the application on exit until this was added.
/// </remarks>
public sealed class LiveActivityFeed : ILiveActivityFeed, IDisposable
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly Uri _hubUri;
    private HubConnection? _connection;

    /// <summary>Creates the stream.</summary>
    /// <param name="serverBaseAddress">Where the server is.</param>
    public LiveActivityFeed(Uri serverBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(serverBaseAddress);
        _hubUri = new Uri(serverBaseAddress, "/hubs/events");
    }

    /// <inheritdoc />
    public event Action<AuditEventSummary>? ActivityReceived;

    /// <inheritdoc />
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <inheritdoc />
    public async Task ConnectAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUri, options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AuditEventSummary>("Activity", activity =>
        {
            // SignalR raises this on a background thread. Everything downstream touches an
            // observable collection bound to the UI, so the hop is made here once rather than
            // remembered at every handler.
            Dispatcher.UIThread.Post(() => ActivityReceived?.Invoke(activity));
        });

        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    /// <summary>
    /// Closes the connection during a synchronous shutdown.
    /// </summary>
    /// <remarks>
    /// Waits, but not indefinitely. The process is on its way out and a hub that will not close
    /// must not be able to hang the window; two seconds is far longer than a local disconnect
    /// takes.
    /// </remarks>
    public void Dispose()
    {
        var connection = _connection;
        _connection = null;

        if (connection is null)
        {
            return;
        }

        try
        {
            connection.DisposeAsync().AsTask().Wait(ShutdownGrace);
        }
        catch (AggregateException)
        {
            // Already faulted or torn down by the transport. Nothing left to close.
        }
    }
}
