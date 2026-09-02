using System.Text;
using KeyManagement.Application.Abstractions;
using KeyManagement.Infrastructure;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using KeyManagement.Server;
using KeyManagement.Server.Devices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// No built-in fallback key. A default would be the same key on every deployment, which is
// indistinguishable from having no key at all, so the server refuses to start instead.
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
{
    throw new InvalidOperationException(
        $"Configure {JwtOptions.SectionName}:SigningKey with at least 32 bytes. " +
        "Use user-secrets in development and an environment variable in deployment; never commit it.");
}

var connectionString = builder.Configuration.GetConnectionString("KeyManagement")
    ?? "Data Source=key-management.db";

builder.Services
    .AddKeyManagementPersistence(connectionString)
    .AddKeyManagementUseCases()
    .AddKeyManagementTokens(jwt);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,

            // The default five minutes of tolerance outlives a fair share of a fifteen-minute
            // access token. Clocks here are on one network and can be expected to agree.
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

var authorization = builder.Services.AddAuthorizationBuilder();
foreach (var permission in Authorization.All)
{
    // One policy per permission, satisfied by the matching claim the token carries.
    authorization.AddPolicy(permission, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(JwtTokenIssuer.PermissionClaimType, permission));
}

var gateway = builder.Configuration.GetSection(DeviceGatewayOptions.SectionName)
    .Get<DeviceGatewayOptions>() ?? new DeviceGatewayOptions();

builder.Services.AddSingleton(gateway);
builder.Services.AddSingleton<CabinetRegistry>();
builder.Services.AddSingleton<ICabinetGateway>(s => s.GetRequiredService<CabinetRegistry>());
builder.Services.AddSingleton<DeviceGatewayService>();
builder.Services.AddHostedService(s => s.GetRequiredService<DeviceGatewayService>());

builder.Services.AddSignalR();
builder.Services.AddScoped<ICustodyEventPublisher, SignalRCustodyEventPublisher>();
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var context = scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>();
    await context.Database.MigrateAsync();

    // Seeding only writes into an empty database, so this is safe to run on every start and
    // means a fresh deployment has an account to sign in with.
    var initialPassword = builder.Configuration["Seed:AdministratorPassword"];
    if (!string.IsNullOrWhiteSpace(initialPassword))
    {
        var cabinetCredential = builder.Configuration["Seed:CabinetCredential"] ?? initialPassword;
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>()
            .SeedAsync(initialPassword, cabinetCredential);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Liveness only. It reports that the host is up, never that custody state is consistent —
// a probe that consults the database would fail the host during a transient lock.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous()
   .WithName("Health");

app.MapKeyManagementEndpoints();
app.MapHub<CustodyHub>(CustodyHub.Path);

await app.RunAsync();

/// <summary>
/// Named so integration tests can reference the entry point through WebApplicationFactory.
/// </summary>
public partial class Program;
