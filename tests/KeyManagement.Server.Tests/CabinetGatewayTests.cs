using System.Net.Http.Json;
using System.Net.Sockets;
using KeyManagement.Contracts;
using KeyManagement.Devices.Protocol;
using KeyManagement.Server.Devices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Server.Tests;

/// <summary>
/// The real server with its listener running, reached over a real socket.
/// </summary>
/// <remarks>
/// Nothing is substituted here. The frames on this connection are the frames a cabinet sends,
/// which is the point: an in-process fake can only fail in the ways it was written to fail in.
/// </remarks>
public sealed class CabinetGatewayApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Password the seeded administrator is created with.</summary>
    public const string AdministratorPassword = "correct horse battery staple";

    /// <summary>Secret the seeded cabinet must present.</summary>
    public const string CabinetCredential = "cabinet-shared-secret";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"kms-gateway-{Guid.CreateVersion7():N}.db");

    /// <summary>The port the gateway actually bound.</summary>
    public int GatewayPort { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var gateway = Services.GetRequiredService<DeviceGatewayService>();

        // Port zero, so a test never collides with whatever else is on the machine. The host
        // starts the listener in the background, so wait for it to say what it took.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (gateway.BoundPort == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }

        GatewayPort = gateway.BoundPort;
    }

    /// <summary>Signs in and returns a client carrying the bearer token.</summary>
    /// <returns>An authenticated client.</returns>
    public async Task<HttpClient> SignInAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", AdministratorPassword));
        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<CommandResult<SessionResponse>>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.Data!.AccessToken);

        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:KeyManagement", $"Data Source={_databasePath}");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-for-hmac-sha256");
        builder.UseSetting("Seed:AdministratorPassword", AdministratorPassword);
        builder.UseSetting("Seed:CabinetCredential", CabinetCredential);
        builder.UseSetting("DeviceGateway:Enabled", "true");
        builder.UseSetting("DeviceGateway:Port", "0");
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A pooled handle not yet released; the file is in the temp directory.
            }
        }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();
}

/// <summary>
/// A cabinet, as far as the server is concerned.
/// </summary>
public sealed class FakeCabinet : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private long _sequence;

    private FakeCabinet(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>Connects and completes the handshake.</summary>
    /// <param name="port">The gateway's port.</param>
    /// <param name="name">The cabinet name to claim.</param>
    /// <param name="credential">The secret to present.</param>
    /// <param name="lastSequenceSent">The highest sequence it says it has sent.</param>
    /// <returns>The cabinet and the server's answer.</returns>
    public static async Task<(FakeCabinet Cabinet, HelloAck Ack)> AttachAsync(
        int port,
        string name = "Reception",
        string credential = CabinetGatewayApi.CabinetCredential,
        long lastSequenceSent = 0)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var cabinet = new FakeCabinet(client);

        await cabinet.SendAsync(
            MessageType.Hello,
            new Hello(name, credential, "1.4.2", ProtocolLimits.Version, lastSequenceSent));

        var ack = await cabinet.ReceiveAsync<HelloAck>(MessageType.HelloAck);

        // Resume where the server says it got to. A cabinet that restarted its numbering would
        // have everything it sends discarded as already applied, which is the sequence check
        // working rather than failing.
        cabinet._sequence = ack.LastAppliedSequence;

        return (cabinet, ack);
    }

    /// <summary>Sends a frame.</summary>
    /// <typeparam name="T">The message.</typeparam>
    /// <param name="type">What the frame carries.</param>
    /// <param name="message">The message.</param>
    /// <returns>A task that completes once written.</returns>
    public ValueTask SendAsync<T>(MessageType type, T message) =>
        FrameCodec.WriteAsync(_stream, type, message);

    /// <summary>Reports a position change, allocating the next sequence number.</summary>
    /// <param name="position">Which position.</param>
    /// <param name="state">What it changed to.</param>
    /// <returns>The sequence number used.</returns>
    public async Task<long> ReportAsync(string position, string state)
    {
        var sequence = ++_sequence;
        await SendAsync(
            MessageType.SlotStateChanged,
            new SlotStateChanged(sequence, position, state, DateTimeOffset.UtcNow));

        return sequence;
    }

    /// <summary>Sends a report again, exactly as it was sent before.</summary>
    /// <param name="sequence">The sequence number to reuse.</param>
    /// <param name="position">Which position.</param>
    /// <param name="state">What it changed to.</param>
    /// <returns>A task that completes once written.</returns>
    public ValueTask ReplayAsync(long sequence, string position, string state) =>
        SendAsync(
            MessageType.SlotStateChanged,
            new SlotStateChanged(sequence, position, state, DateTimeOffset.UtcNow));

    /// <summary>Reads the next frame and decodes it as the expected message.</summary>
    /// <typeparam name="T">The expected message.</typeparam>
    /// <param name="expected">The message type that must arrive.</param>
    /// <returns>The message.</returns>
    public async Task<T> ReceiveAsync<T>(MessageType expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = await FrameCodec.ReadAsync(_stream, timeout.Token)
            ?? throw new InvalidOperationException("The server closed the connection.");

        Assert.Equal(expected, frame.Type);
        return FrameCodec.Decode<T>(frame);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The custody loop, closed by a cabinet on the other end of a socket.
/// </summary>
public sealed class CabinetGatewayTests : IClassFixture<CabinetGatewayApi>
{
    private readonly CabinetGatewayApi _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">The running server and its listener.</param>
    public CabinetGatewayTests(CabinetGatewayApi api) => _api = api;

    [Fact]
    public async Task A_cabinet_with_the_wrong_credential_is_refused()
    {
        var (cabinet, ack) = await FakeCabinet.AttachAsync(
            _api.GatewayPort, credential: "not the secret");
        await using var _ = cabinet;

        Assert.False(ack.Accepted);
        Assert.NotNull(ack.Reason);
    }

    [Fact]
    public async Task An_unknown_cabinet_is_refused_the_same_way_as_a_wrong_credential()
    {
        // The answer must not tell a guesser which half they got right.
        var (unknown, unknownAck) = await FakeCabinet.AttachAsync(
            _api.GatewayPort, name: "Nowhere", credential: "not the secret");
        await using var _ = unknown;

        var (real, wrongSecret) = await FakeCabinet.AttachAsync(
            _api.GatewayPort, credential: "not the secret");
        await using var __ = real;

        Assert.False(unknownAck.Accepted);
        Assert.False(wrongSecret.Accepted);
        Assert.Equal(unknownAck.Reason, wrongSecret.Reason);
    }

    [Fact]
    public async Task An_unsupported_protocol_version_is_refused()
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _api.GatewayPort);
        await using var stream = client.GetStream();

        await FrameCodec.WriteAsync(
            stream,
            MessageType.Hello,
            new Hello("Reception", CabinetGatewayApi.CabinetCredential, "1.4.2", 99, 0));

        var frame = await FrameCodec.ReadAsync(stream);
        var ack = FrameCodec.Decode<HelloAck>(frame!.Value);

        Assert.False(ack.Accepted);
        client.Dispose();
    }

    [Fact]
    public async Task An_item_is_released_taken_and_returned_across_the_wire()
    {
        var (cabinet, ack) = await FakeCabinet.AttachAsync(_api.GatewayPort);
        await using var _ = cabinet;
        Assert.True(ack.Accepted);

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A01");

        // The release only succeeds because a cabinet is now attached.
        var requested = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, null));
        var checkout = await requested.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);

        // The command reaches the cabinet, carrying the correlation id of the request.
        var unlock = await cabinet.ReceiveAsync<UnlockSlot>(MessageType.UnlockSlot);
        Assert.Equal("A01", unlock.Position);
        Assert.Equal(checkout.CorrelationId, unlock.CorrelationId);

        await cabinet.SendAsync(
            MessageType.CommandOutcome,
            new CommandOutcome(unlock.CorrelationId, true, null, DateTimeOffset.UtcNow));

        // Until this arrives the item is only released. This is what makes it custody.
        var taken = await cabinet.ReportAsync("A01", "Empty");
        await WaitForStateAsync(client, target.Id, "CheckedOut");

        var returned = await cabinet.ReportAsync("A01", "Occupied");
        await WaitForStateAsync(client, target.Id, "Available");

        Assert.True(returned > taken);
    }

    [Fact]
    public async Task A_replayed_report_does_not_move_custody_twice()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api.GatewayPort);
        await using var __ = cabinet;

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A02");

        var requested = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, null));
        var checkout = await requested.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);
        await cabinet.ReceiveAsync<UnlockSlot>(MessageType.UnlockSlot);

        var sequence = await cabinet.ReportAsync("A02", "Empty");
        await WaitForStateAsync(client, target.Id, "CheckedOut");

        // A reconnecting cabinet replays what it is not sure the server received. The same
        // sequence number arriving twice must not check the item out twice.
        await cabinet.ReplayAsync(sequence, "A02", "Empty");
        await cabinet.ReplayAsync(sequence, "A02", "Empty");
        await Task.Delay(400);

        var after = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        Assert.Equal("CheckedOut", after!.Single(i => i.Id == target.Id).CustodyState);

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");
        Assert.Single(trail!, e => e.Type == "CheckoutCompleted");
    }

    [Fact]
    public async Task A_position_emptying_with_no_release_behind_it_raises_an_alarm()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api.GatewayPort);
        await using var __ = cabinet;

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A03");

        // Nobody asked for this key. The cabinet says it left anyway.
        await cabinet.ReportAsync("A03", "Empty");

        // Custody becomes uncertain rather than the trail recording a checkout nobody made.
        await WaitForStateAsync(client, target.Id, "Unknown");

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");
        Assert.Contains(trail!, e => e.Type == "UnauthorizedSlotChange");
    }

    [Fact]
    public async Task A_cabinet_that_drops_leaves_its_positions_unconfirmed()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api.GatewayPort);
        var client = await _api.SignInAsync();

        var cabinets = await client.GetFromJsonAsync<List<CabinetSummary>>("/api/cabinets");
        Assert.Equal("Online", cabinets!.Single().Status);

        await cabinet.DisposeAsync();

        // A stale reading shown as current is the failure this avoids.
        await WaitForAsync(async () =>
        {
            var after = await client.GetFromJsonAsync<List<CabinetSummary>>("/api/cabinets");
            return after!.Single().Status == "Offline";
        });
    }

    private static async Task WaitForStateAsync(HttpClient client, Guid itemId, string state) =>
        await WaitForAsync(async () =>
        {
            var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
            return items!.Single(i => i.Id == itemId).CustodyState == state;
        });

    // The server applies a report on its own thread, so the HTTP side is polled rather than
    // assumed. A fixed sleep would either be flaky or slow; this is neither.
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (!await condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, timeout.Token);
        }
    }
}
