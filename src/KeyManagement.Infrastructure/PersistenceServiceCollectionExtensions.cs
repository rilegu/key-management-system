using KeyManagement.Application.Abstractions;
using KeyManagement.Application.Administration;
using KeyManagement.Application.Alarms;
using KeyManagement.Application.Authentication;
using KeyManagement.Application.Custody;
using KeyManagement.Application.Devices;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using KeyManagement.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KeyManagement.Infrastructure;

/// <summary>
/// Registers the persistence layer, the use cases and the services they depend on.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>How long a blocked writer waits before giving up.</summary>
    /// <remarks>
    /// SQLite allows one writer at a time. Five seconds is far longer than any write here
    /// takes, so a timeout means something is genuinely stuck rather than merely contended.
    /// </remarks>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Adds the database, the repositories, the hasher and the clock.</summary>
    /// <param name="services">The container.</param>
    /// <param name="connectionString">SQLite connection string.</param>
    /// <returns>The container, for chaining.</returns>
    public static IServiceCollection AddKeyManagementPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<KeyManagementDbContext>(options =>
            options
                .UseSqlite(connectionString)
                .AddInterceptors(
                    new SqlitePragmaInterceptor(DefaultBusyTimeout),
                    new AppendOnlyAuditInterceptor()));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<ICabinetRepository, CabinetRepository>();
        services.AddScoped<ICheckoutRepository, CheckoutRepository>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IAlarmRepository, AlarmRepository>();
        services.AddScoped<IAdministrationStore, AdministrationStore>();
        services.AddScoped<IDeviceEventLog, DeviceEventLog>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICustodyQueries, CustodyQueries>();

        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<CabinetEnrolment>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>Adds the use cases.</summary>
    /// <param name="services">The container.</param>
    /// <returns>The container, for chaining.</returns>
    public static IServiceCollection AddKeyManagementUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<SignInService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<CabinetEventService>();
        services.AddScoped<AlarmService>();
        services.AddScoped<AdministrationService>();
        services.AddScoped<CustodySweep>();

        // Only added if the host has not supplied a real transport, so the server's
        // SignalR publisher wins wherever one is registered.
        services.TryAddSingleton<ICustodyEventPublisher, NullCustodyEventPublisher>();

        // Likewise for the device layer: without a listener every cabinet is unreachable, which
        // is the truth rather than a stub.
        services.TryAddSingleton<ICabinetGateway, NullCabinetGateway>();

        return services;
    }

    /// <summary>Adds token issuing.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">Signing and lifetime configuration.</param>
    /// <returns>The container, for chaining.</returns>
    public static IServiceCollection AddKeyManagementTokens(
        this IServiceCollection services,
        JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<ITokenIssuer>(_ => new JwtTokenIssuer(options));

        return services;
    }
}
