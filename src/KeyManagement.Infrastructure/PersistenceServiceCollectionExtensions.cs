using KeyManagement.Application.Abstractions;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using KeyManagement.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure;

/// <summary>
/// Registers the persistence layer.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>How long a blocked writer waits before giving up.</summary>
    /// <remarks>
    /// SQLite allows one writer at a time. Five seconds is far longer than any write here
    /// takes, so a timeout means something is genuinely stuck rather than merely contended.
    /// </remarks>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Adds the database, the password hasher and the clock.</summary>
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

        services.AddScoped<DatabaseSeeder>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
