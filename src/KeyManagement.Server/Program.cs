using System.Text;
using KeyManagement.Application.Abstractions;
using KeyManagement.Infrastructure;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using KeyManagement.Server;
using KeyManagement.Server.Devices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;

// Running as a Windows Service, the working directory is the system directory rather than the
// installation folder, so every relative path in configuration would resolve somewhere
// unexpected. The content root has to be set when the builder is created — changing it
// afterwards is refused — and only when there is actually a service, so a test host keeps its
// own root.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Services.AddWindowsService(options => options.ServiceName = "Key Management");

var hosting = builder.Configuration.GetSection(HostingOptions.SectionName)
    .Get<HostingOptions>() ?? new HostingOptions();

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

var certificates = builder.Configuration.GetSection(DeviceCertificateOptions.SectionName)
    .Get<DeviceCertificateOptions>() ?? new DeviceCertificateOptions();

// A process that does not run the device layer must not open its port, whatever the gateway
// section says. The role is the deployment's intent; the section only configures it.
gateway.Enabled = gateway.Enabled && hosting.RunsDevices;

// The device authority protects the key that lets a new cabinet be enrolled. As with the
// signing key, there is no built-in default: one would be the same key everywhere.
if (gateway.Enabled && string.IsNullOrWhiteSpace(certificates.Password))
{
    throw new InvalidOperationException(
        $"Configure {DeviceCertificateOptions.SectionName}:Password before enabling the device gateway. " +
        "Use user-secrets in development and an environment variable in deployment; never commit it.");
}

builder.Services.AddSingleton(hosting);
builder.Services.AddSingleton(certificates);
builder.Services.AddSingleton(gateway);
builder.Services.AddSingleton<CabinetRegistry>();
builder.Services.AddSingleton<ICabinetGateway>(s => s.GetRequiredService<CabinetRegistry>());
builder.Services.AddSingleton<DeviceGatewayService>();

var sweep = builder.Configuration.GetSection(CustodySweepOptions.SectionName)
    .Get<CustodySweepOptions>() ?? new CustodySweepOptions();

sweep.Enabled = sweep.Enabled && hosting.RunsDevices;
builder.Services.AddSingleton(sweep);

if (hosting.RunsDevices)
{
    builder.Services.AddHostedService(s => s.GetRequiredService<DeviceGatewayService>());
    builder.Services.AddHostedService<CustodySweepService>();
}

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
        var administratorPin = builder.Configuration["Seed:AdministratorPin"] ?? "1234";
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>()
            .SeedAsync(initialPassword, administratorPin);
    }
}

// Enrolment is a one-off act by a person installing a cabinet, so it runs and exits rather
// than starting a server nobody asked for.
if (args is ["--issue-cabinet-certificate", var cabinetToEnrol, ..])
{
    await using var enrolmentScope = app.Services.CreateAsyncScope();
    var enrolment = enrolmentScope.ServiceProvider.GetRequiredService<CabinetEnrolment>();
    var (issuedPath, issuedThumbprint) = await enrolment.IssueAsync(cabinetToEnrol);

    Console.WriteLine($"Issued a certificate for '{cabinetToEnrol}'.");
    Console.WriteLine($"  File:        {issuedPath}");
    Console.WriteLine($"  Fingerprint: {issuedThumbprint}");
    Console.WriteLine("Copy the file to the cabinet. It is the only thing that can now attach under that name.");
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Liveness only. It reports that the host is up, never that custody state is consistent —
// a probe that consults the database would fail the host during a transient lock. Mapped in
// every role, because a gateway-only process still has to be watchable.
app.MapGet("/health", () => Results.Ok(new { status = "ok", role = hosting.Role.ToString() }))
   .AllowAnonymous()
   .WithName("Health");

if (hosting.RunsApi)
{
    app.MapKeyManagementEndpoints();
    app.MapHub<CustodyHub>(CustodyHub.Path);
}

await app.RunAsync();

/// <summary>
/// Named so integration tests can reference the entry point through WebApplicationFactory.
/// </summary>
public partial class Program;
