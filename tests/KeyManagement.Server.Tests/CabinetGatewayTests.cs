using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using KeyManagement.Contracts;
using KeyManagement.Devices.Protocol;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using KeyManagement.Server.Devices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Server.Tests;

/// <summary>
/// The real server with its listener running, reached over a real mutually authenticated socket.
/// </summary>
/// <remarks>
/// Nothing is substituted. The handshake, the certificates and the frames are the ones a cabinet
/// uses, which is the point: an in-process fake can only fail in the ways it was written to.
/// </remarks>
public sealed class CabinetGatewayApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Password the seeded administrator is created with.</summary>
    public const string AdministratorPassword = "correct horse battery staple";

    /// <summary>PIN the seeded administrator enters at a keypad.</summary>
    public const string AdministratorPin = "4821";

    /// <summary>Protects the private keys these tests generate.</summary>
    public const string CertificatePassword = "test-certificate-password";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"kms-gateway-{Guid.CreateVersion7():N}.db");

    private readonly string _certificateDirectory = Path.Combine(
        Path.GetTempPath(), $"kms-certs-{Guid.CreateVersion7():N}");

    /// <summary>The port the gateway actually bound.</summary>
    public int GatewayPort { get; private set; }

    /// <summary>Where this fixture's certificates live, for a simulator to read.</summary>
    public string CertificateDirectory => _certificateDirectory;

    /// <summary>The authority every certificate here chains to.</summary>
    public X509Certificate2 Authority { get; private set; } = null!;

    /// <summary>The certificate enrolled to the seeded cabinet.</summary>
    public X509Certificate2 ReceptionCertificate { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Enrolment is what a person does before a cabinet can attach, so the test does it too
        // rather than reaching into the database.
        await using (var scope = Services.CreateAsyncScope())
        {
            var enrolment = scope.ServiceProvider.GetRequiredService<CabinetEnrolment>();
            var (path, _) = await enrolment.IssueAsync("Reception");
            ReceptionCertificate = CabinetCertificates.Load(path, CertificatePassword);
            Authority = enrolment.EnsureAuthority();
        }

        var gateway = Services.GetRequiredService<DeviceGatewayService>();

        // Port zero, so a test never collides with whatever else is on the machine.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (gateway.BoundPort == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }

        GatewayPort = gateway.BoundPort;
    }

    /// <summary>Issues a certificate that chains correctly but names a different cabinet.</summary>
    /// <param name="commonName">The name to issue it to.</param>
    /// <returns>The certificate.</returns>
    public X509Certificate2 IssueUnenrolled(string commonName) =>
        CabinetCertificates.Issue(
            Authority, commonName, CertificatePurpose.Cabinet, DateTimeOffset.UtcNow);

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

    /// <summary>Runs work against the server's own database.</summary>
    /// <param name="work">The work.</param>
    /// <returns>A task that completes when the work does.</returns>
    public async Task WithContextAsync(Func<KeyManagementDbContext, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>());
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:KeyManagement", $"Data Source={_databasePath}");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-for-hmac-sha256");
        builder.UseSetting("Seed:AdministratorPassword", AdministratorPassword);
        builder.UseSetting("Seed:AdministratorPin", AdministratorPin);
        builder.UseSetting("DeviceGateway:Enabled", "true");
        builder.UseSetting("DeviceGateway:Port", "0");
        builder.UseSetting("DeviceCertificates:Directory", _certificateDirectory);
        builder.UseSetting("DeviceCertificates:Password", CertificatePassword);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);

        ReceptionCertificate?.Dispose();
        Authority?.Dispose();

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

        try
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or DirectoryNotFoundException)
        {
            // Same reasoning: cleanup must not fail a passing test.
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
    private readonly SslStream _stream;
    private long _sequence;

    private FakeCabinet(TcpClient client, SslStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>Connects with mutual TLS and completes the handshake.</summary>
    /// <param name="api">The running server.</param>
    /// <param name="name">The cabinet name to claim.</param>
    /// <param name="certificate">The certificate to present, or the enrolled one.</param>
    /// <returns>The cabinet and the server's answer.</returns>
    public static async Task<(FakeCabinet Cabinet, HelloAck Ack)> AttachAsync(
        CabinetGatewayApi api,
        string name = "Reception",
        X509Certificate2? certificate = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        var stream = await ConnectAsync(api, certificate ?? api.ReceptionCertificate);
        var cabinet = new FakeCabinet(stream.Client, stream.Ssl);

        await cabinet.SendAsync(
            MessageType.Hello, new Hello(name, "1.4.2", ProtocolLimits.Version, 0));

        var ack = await cabinet.ReceiveAsync<HelloAck>(MessageType.HelloAck);

        // Resume where the server says it got to. A cabinet that restarted its numbering would
        // have everything discarded as already applied, which is the sequence check working.
        cabinet._sequence = ack.LastAppliedSequence;

        return (cabinet, ack);
    }

    /// <summary>Negotiates mutual TLS without saying Hello.</summary>
    /// <param name="api">The running server.</param>
    /// <param name="certificate">The certificate to present, if any.</param>
    /// <returns>The connection.</returns>
    public static async Task<(TcpClient Client, SslStream Ssl)> ConnectAsync(
        CabinetGatewayApi api,
        X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(api);

        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", api.GatewayPort);

        var ssl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, presented, _, _) => presented is not null);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ClientCertificates = certificate is null ? null : [certificate],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });

        return (client, ssl);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var frame = await FrameCodec.ReadAsync(_stream, timeout.Token)
            ?? throw new InvalidOperationException("The server closed the connection.");

        Assert.Equal(expected, frame.Type);
        return FrameCodec.Decode<T>(frame);
    }

    /// <summary>Reads the release and the keypad answer, in whichever order they arrive.</summary>
    /// <returns>Both messages.</returns>
    public async Task<(UnlockSlot Unlock, AccessResult Answer)> ReceiveUnlockAndAnswerAsync()
    {
        UnlockSlot? unlock = null;
        AccessResult? answer = null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (unlock is null || answer is null)
        {
            var frame = await FrameCodec.ReadAsync(_stream, timeout.Token)
                ?? throw new InvalidOperationException("The server closed the connection.");

            switch (frame.Type)
            {
                case MessageType.UnlockSlot:
                    unlock = FrameCodec.Decode<UnlockSlot>(frame);
                    break;

                case MessageType.AccessResult:
                    answer = FrameCodec.Decode<AccessResult>(frame);

                    // A refusal releases nothing, so there is no unlock to wait for.
                    if (!answer.Granted)
                    {
                        return (new UnlockSlot(Guid.Empty, string.Empty, TimeSpan.Zero), answer);
                    }

                    break;

                default:
                    break;
            }
        }

        return (unlock, answer);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}

/// <summary>
/// What the mutually authenticated link accepts and what it refuses.
/// </summary>
public sealed class CabinetTlsTests : IClassFixture<CabinetGatewayApi>
{
    private readonly CabinetGatewayApi _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">The running server and its listener.</param>
    public CabinetTlsTests(CabinetGatewayApi api) => _api = api;

    [Fact]
    public async Task A_plaintext_connection_never_becomes_a_session()
    {
        // The whole point of authenticating the link. A peer that speaks the protocol but not TLS gets
        // nowhere, so a network attacker cannot simply frame a Hello and be believed.
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _api.GatewayPort);
        await using var stream = client.GetStream();

        await FrameCodec.WriteAsync(
            stream, MessageType.Hello, new Hello("Reception", "1.4.2", ProtocolLimits.Version, 0));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Either the server drops it, or the bytes it sends back are a TLS alert rather than a
        // frame. Both are a refusal; neither is a HelloAck.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var frame = await FrameCodec.ReadAsync(stream, timeout.Token)
                ?? throw new IOException("The server closed the connection.");
            Assert.NotEqual(MessageType.HelloAck, frame.Type);
        });
    }

    [Fact]
    public async Task A_connection_offering_no_certificate_is_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (client, ssl) = await FakeCabinet.ConnectAsync(_api, certificate: null);

            using (client)
            await using (ssl)
            {
                await FrameCodec.WriteAsync(
                    ssl, MessageType.Hello, new Hello("Reception", "1.4.2", ProtocolLimits.Version, 0));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var frame = await FrameCodec.ReadAsync(ssl, timeout.Token) ?? throw new IOException("The server closed the connection.");
            }
        });
    }

    [Fact]
    public async Task A_certificate_this_deployment_did_not_issue_is_refused()
    {
        using var stranger = CabinetCertificates.CreateAuthority(DateTimeOffset.UtcNow);
        using var forged = CabinetCertificates.Issue(
            stranger, "Reception", CertificatePurpose.Cabinet, DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (client, ssl) = await FakeCabinet.ConnectAsync(_api, forged);

            using (client)
            await using (ssl)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var frame = await FrameCodec.ReadAsync(ssl, timeout.Token) ?? throw new IOException("The server closed the connection.");
            }
        });
    }

    [Fact]
    public async Task A_valid_certificate_for_a_different_cabinet_cannot_attach_as_this_one()
    {
        // Chains to the right authority, so TLS is happy. It is still not the certificate this
        // cabinet was enrolled with, and the name on it disagrees with the name claimed.
        using var elsewhere = _api.IssueUnenrolled("Loading bay");

        var (cabinet, ack) = await FakeCabinet.AttachAsync(_api, "Reception", elsewhere);
        await using var _ = cabinet;

        Assert.False(ack.Accepted);
        Assert.NotNull(ack.Reason);
    }

    [Fact]
    public async Task The_enrolled_certificate_attaches()
    {
        var (cabinet, ack) = await FakeCabinet.AttachAsync(_api);
        await using var _ = cabinet;

        Assert.True(ack.Accepted);
        Assert.NotEqual(Guid.Empty, ack.SessionId);
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
    public async Task An_item_is_released_taken_and_returned_across_the_wire()
    {
        var (cabinet, ack) = await FakeCabinet.AttachAsync(_api);
        await using var _ = cabinet;
        Assert.True(ack.Accepted);

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A01");

        var requested = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, null));
        var checkout = await requested.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);

        var unlock = await cabinet.ReceiveAsync<UnlockSlot>(MessageType.UnlockSlot);
        Assert.Equal("A01", unlock.Position);
        Assert.Equal(checkout.CorrelationId, unlock.CorrelationId);

        await cabinet.SendAsync(
            MessageType.CommandOutcome,
            new CommandOutcome(unlock.CorrelationId, true, null, DateTimeOffset.UtcNow));

        // Until this arrives the item is only released. This is what makes it custody.
        await cabinet.ReportAsync("A01", "Empty");
        await WaitForStateAsync(client, target.Id, "CheckedOut");

        await cabinet.ReportAsync("A01", "Occupied");
        await WaitForStateAsync(client, target.Id, "Available");
    }

    [Fact]
    public async Task Someone_at_the_keypad_is_judged_by_the_same_rules_as_a_workstation()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api);
        await using var __ = cabinet;

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A04");

        await cabinet.SendAsync(
            MessageType.AccessRequest,
            new AccessRequest(
                Guid.CreateVersion7(), "A04", "admin", CabinetGatewayApi.AdministratorPin));

        // The unlock is dispatched by the custody service itself and the answer is sent by the
        // session afterwards, so the unlock arrives first. Asserting on arrival rather than on
        // order keeps this test about the outcome instead of about that detail.
        var (unlock, answer) = await cabinet.ReceiveUnlockAndAnswerAsync();

        Assert.True(answer.Granted);
        Assert.Equal("A04", unlock.Position);

        await cabinet.ReportAsync("A04", "Empty");
        await WaitForStateAsync(client, target.Id, "CheckedOut");
    }

    [Fact]
    public async Task A_wrong_pin_is_refused_and_nothing_is_released()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api);
        await using var __ = cabinet;

        await cabinet.SendAsync(
            MessageType.AccessRequest,
            new AccessRequest(Guid.CreateVersion7(), "A05", "admin", "0000"));

        var answer = await cabinet.ReceiveAsync<AccessResult>(MessageType.AccessResult);

        Assert.False(answer.Granted);
        Assert.Contains("not correct", answer.Message, StringComparison.OrdinalIgnoreCase);

        var client = await _api.SignInAsync();
        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            "/api/audit-events?type=SignInFailed&take=50");
        Assert.NotEmpty(trail!);
    }

    [Fact]
    public async Task A_replayed_report_does_not_move_custody_twice()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api);
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

        // A reconnecting cabinet replays what it is not sure the server received.
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
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api);
        await using var __ = cabinet;

        var client = await _api.SignInAsync();
        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A03");

        // Nobody asked for this key. The cabinet says it left anyway.
        await cabinet.ReportAsync("A03", "Empty");
        await WaitForStateAsync(client, target.Id, "Unknown");

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");
        Assert.Contains(trail!, e => e.Type == "UnauthorizedSlotChange");
    }

    [Fact]
    public async Task A_cabinet_that_drops_leaves_its_positions_unconfirmed()
    {
        var (cabinet, _) = await FakeCabinet.AttachAsync(_api);
        var client = await _api.SignInAsync();

        await WaitForAsync(async () =>
        {
            var attached = await client.GetFromJsonAsync<List<CabinetSummary>>("/api/cabinets");
            return attached!.Single().Status == "Online";
        });

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!await condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, timeout.Token);
        }
    }
}
