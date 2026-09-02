using System.Net.Http.Headers;
using System.Net.Http.Json;
using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KeyManagement.Server.Tests;

/// <summary>
/// The real server over a temporary SQLite file, reached the way a client reaches it.
/// </summary>
/// <remarks>
/// Nothing is substituted but the database path and the signing key. Authentication,
/// authorization policies, the interceptors and the endpoint routing are the ones that ship,
/// so a policy wired to the wrong claim fails here rather than in the client sprint.
/// </remarks>
public sealed class KeyManagementApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Password the seeded administrator is created with.</summary>
    public const string AdministratorPassword = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"kms-api-{Guid.CreateVersion7():N}.db");

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        // Forces the host to build, which runs migration and seeding in Program.
        _ = Services;
        return Task.CompletedTask;
    }

    /// <summary>Signs in and returns a client carrying the resulting bearer token.</summary>
    /// <param name="userName">Sign-in name.</param>
    /// <param name="password">Password.</param>
    /// <returns>An authenticated client.</returns>
    public async Task<HttpClient> SignInAsync(
        string userName = "admin",
        string password = AdministratorPassword)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(userName, password));
        response.EnsureSuccessStatusCode();

        var session = await response.Content
            .ReadFromJsonAsync<CommandResult<SessionResponse>>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session!.Data!.AccessToken);

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

        // These tests are about the HTTP surface, not the wire. Custody refuses a release when
        // the cabinet cannot be reached, so a stand-in reports the seeded cabinet as attached;
        // CabinetGatewayTests exercises the real listener over a real socket.
        builder.ConfigureTestServices(services =>
            services.Replace(
                ServiceDescriptor.Singleton<ICabinetGateway, AlwaysAttachedGateway>()));
    }

    private sealed class AlwaysAttachedGateway : ICabinetGateway
    {
        public bool IsAttached(Domain.CabinetId cabinetId) => true;

        public Task<bool> UnlockAsync(
            Domain.CabinetId cabinetId,
            string position,
            Domain.CorrelationId correlationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);

        // WAL mode leaves two sidecar files next to the database.
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A pooled handle not yet released. It is in the temp directory, and failing a
                // passing test over cleanup would be the worse outcome.
            }
        }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();
}
