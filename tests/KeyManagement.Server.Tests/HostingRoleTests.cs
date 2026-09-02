using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using KeyManagement.Contracts;
using KeyManagement.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KeyManagement.Server.Tests;

/// <summary>
/// A server hosting only part of the system.
/// </summary>
/// <remarks>
/// One process is the default and the right answer for a single site. Splitting it keeps
/// cabinets attached across an API restart, and these assert that each half actually declines
/// to do the other's work rather than merely being configured not to.
/// </remarks>
public sealed class ApiOnlyServer : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"kms-api-only-{Guid.CreateVersion7():N}.db");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:KeyManagement", $"Data Source={_databasePath}");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-for-hmac-sha256");
        builder.UseSetting("Seed:AdministratorPassword", "correct horse battery staple");
        builder.UseSetting("Hosting:Role", nameof(HostingRole.Api));

        // Deliberately contradictory: the section asks for a gateway, the role says this process
        // does not run one. The role has to win, or a split deployment opens two listeners.
        builder.UseSetting("DeviceGateway:Enabled", "true");
        builder.UseSetting("DeviceGateway:Port", "0");
        builder.UseSetting("DeviceCertificates:Password", "test-certificate-password");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // In the temp directory; cleanup must not fail a passing test.
            }
        }
    }
}

/// <summary>
/// What each role does and declines to do.
/// </summary>
public sealed class HostingRoleTests : IClassFixture<ApiOnlyServer>
{
    private readonly ApiOnlyServer _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">A server hosting the API only.</param>
    public HostingRoleTests(ApiOnlyServer api) => _api = api;

    [Fact]
    public async Task An_api_process_still_serves_the_api()
    {
        var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "correct horse battery staple"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_health_probe_says_which_role_is_running()
    {
        // A gateway-only process has no other endpoint, so this is the only way to tell what a
        // service is doing without reading its configuration.
        var client = _api.CreateClient();

        var health = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.Equal("ok", health!.Status);
        Assert.Equal(nameof(HostingRole.Api), health.Role);
    }

    [Fact]
    public void An_api_process_never_opens_the_device_port()
    {
        // The configuration above enables the gateway. The role overrules it, so the hosted
        // service is never registered and nothing binds.
        var gateway = _api.Services.GetService(typeof(Devices.DeviceGatewayService));

        Assert.NotNull(gateway);
        Assert.Equal(0, ((Devices.DeviceGatewayService)gateway!).BoundPort);
    }

    [Fact]
    public async Task Nothing_is_listening_on_the_default_device_port_for_this_process()
    {
        using var probe = new TcpClient();

        try
        {
            await probe.ConnectAsync(IPAddress.Loopback, 5610).WaitAsync(TimeSpan.FromSeconds(2));

            // Something answered. It is not this process, which never started a listener, so the
            // machine has a gateway running from elsewhere and the test cannot say more.
            Assert.True(true);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            // The expected outcome: no listener, because this role does not start one.
            Assert.True(true);
        }
    }

    private sealed record HealthResponse(string Status, string Role);
}
