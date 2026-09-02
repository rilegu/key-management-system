using System.Net;
using System.Net.Http.Json;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Server.Tests;

/// <summary>
/// The custody loop, walked end to end over HTTP: sign in, check out, be refused, return, read
/// the trail.
/// </summary>
public sealed class CustodyWalkthroughTests : IClassFixture<KeyManagementApi>
{
    private readonly KeyManagementApi _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">The running server.</param>
    public CustodyWalkthroughTests(KeyManagementApi api) => _api = api;

    [Fact]
    public async Task Custody_runs_end_to_end_with_no_hardware()
    {
        var client = await _api.SignInAsync();

        var assets = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = assets!.First(a => a.CustodyState == nameof(AssetCustodyState.Available));

        // Check out.
        var checkoutResponse = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, DateTimeOffset.UtcNow.AddHours(4)));
        checkoutResponse.EnsureSuccessStatusCode();

        var checkout = await checkoutResponse.Content
            .ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);
        Assert.Equal(nameof(CheckoutState.Pending), checkout.State);

        // The cabinet confirms the asset was taken. Until the device layer exists that step
        // is performed directly against the database.
        await _api.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == new AssetId(target.Id));
            var record = await context.Checkouts.SingleAsync(c => c.Id == new CheckoutId(checkout.Data!.Id));
            asset.ConfirmTaken();
            record.ConfirmTaken(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        });

        // What is out now shows it.
        var open = await client.GetFromJsonAsync<List<CheckoutSummary>>("/api/checkouts");
        Assert.Contains(open!, c => c.Id == checkout.Data!.Id);

        // Return.
        var returnResponse = await client.PostAsJsonAsync(
            $"/api/checkouts/{checkout.Data!.Id}/return", new { });
        returnResponse.EnsureSuccessStatusCode();

        var returned = await returnResponse.Content
            .ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(returned!.Success);

        // The trail holds the whole story, newest first.
        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");

        var types = trail!.Select(e => e.Type).ToList();
        Assert.Contains(nameof(Domain.Auditing.AuditEventType.CheckoutRequested), types);
        Assert.Contains(nameof(Domain.Auditing.AuditEventType.CheckoutAuthorized), types);
        Assert.Contains(nameof(Domain.Auditing.AuditEventType.ReturnRequested), types);

        // Every record from one command shares its correlation id.
        Assert.Contains(trail!, e => e.CorrelationId == checkout.CorrelationId);
    }

    [Fact]
    public async Task A_request_outside_the_holders_groups_is_refused_by_the_server()
    {
        // The refusal is decided server-side. A client that offers the button anyway, or a
        // rebuilt client with no button at all, gets the same answer.
        var client = await _api.SignInAsync();

        AssetId ungrouped = default;
        await _api.WithContextAsync(async context =>
        {
            var group = new AssetGroup($"Restricted {Guid.CreateVersion7():N}");
            var asset = new Asset($"RS-{Guid.CreateVersion7():N}"[..12], "Restricted door", group.Id);
            var cabinet = await context.Cabinets.Include(c => c.Slots).FirstAsync();
            var free = cabinet.Slots.First(s => s.AssetId == null);
            free.Assign(asset.Id);

            context.AssetGroups.Add(group);
            context.Assets.Add(asset);
            await context.SaveChangesAsync();
            ungrouped = asset.Id;
        });

        var response = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(ungrouped.Value, null));

        // 200, not 403: a refused custody request is an outcome the system exists to produce
        // and record, not a transport failure.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.False(result!.Success);
        Assert.Equal(nameof(CheckoutState.Denied), result.State);
        Assert.Contains("group", result.Message, StringComparison.OrdinalIgnoreCase);

        await _api.WithContextAsync(async context =>
        {
            var asset = await context.Assets.SingleAsync(a => a.Id == ungrouped);
            Assert.Equal(AssetCustodyState.Available, asset.CustodyState);

            Assert.True(await context.AuditEvents.AnyAsync(
                e => e.Type == Domain.Auditing.AuditEventType.CheckoutDenied));
        });
    }
}

/// <summary>
/// What the API refuses before a use case is ever reached.
/// </summary>
public sealed class ApiAuthorizationTests : IClassFixture<KeyManagementApi>
{
    private readonly KeyManagementApi _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">The running server.</param>
    public ApiAuthorizationTests(KeyManagementApi api) => _api = api;

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/assets")]
    [InlineData("/api/cabinets")]
    [InlineData("/api/checkouts")]
    [InlineData("/api/audit-events")]
    public async Task Every_custody_endpoint_requires_a_token(string path)
    {
        var client = _api.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_without_one()
    {
        var client = _api.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_credentials_are_refused_without_saying_which_half_was_wrong()
    {
        var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "not the password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CommandResult<SessionResponse>>();
        Assert.False(result!.Success);
        Assert.Null(result.Data);
        Assert.Equal("The user name or password is not correct.", result.Message);
    }

    [Fact]
    public async Task A_holder_without_the_audit_permission_is_refused_the_trail()
    {
        // The seeded Holder role carries CheckoutAsset only. This is the policy doing its job
        // rather than the use case: the request never reaches one.
        const string password = "another correct horse";
        await _api.WithContextAsync(async context =>
        {
            var role = await context.Roles.SingleAsync(r => r.Name == "Holder");
            var user = new Domain.Access.User(
                "holder",
                "Holder",
                new Infrastructure.Security.IdentityPasswordHasher().Hash(password));
            user.Grant(role);
            context.Users.Add(user);
            await context.SaveChangesAsync();
        });

        var client = await _api.SignInAsync("holder", password);

        var response = await client.GetAsync(new Uri("/api/audit-events", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_refresh_token_buys_a_new_access_token()
    {
        var client = _api.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin", KeyManagementApi.AdministratorPassword));
        var session = await login.Content.ReadFromJsonAsync<CommandResult<SessionResponse>>();

        var refreshed = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(session!.Data!.RefreshToken));
        refreshed.EnsureSuccessStatusCode();

        var renewed = await refreshed.Content.ReadFromJsonAsync<CommandResult<SessionResponse>>();
        Assert.True(renewed!.Success);
        Assert.NotEqual(session.Data.RefreshToken, renewed.Data!.RefreshToken);
    }
}
