using KeyManagement.Infrastructure;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// A real SQLite file, migrated, wired through the production service registration.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than the EF in-memory provider, which enforces no relational constraint: a
/// unique index, a foreign key or a filtered index would all silently pass there and fail
/// against SQLite. Tests that cannot fail are worse than no tests.
/// </para>
/// <para>
/// Built through <see cref="PersistenceServiceCollectionExtensions.AddKeyManagementPersistence"/>
/// rather than a hand-made context, so the interceptors and pragmas under test are the ones
/// the server will actually run.
/// </para>
/// </remarks>
public sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TemporaryDatabase(ServiceProvider provider, string filePath)
    {
        _provider = provider;
        FilePath = filePath;
    }

    /// <summary>Where the database file lives.</summary>
    public string FilePath { get; }

    /// <summary>Creates and migrates a fresh database.</summary>
    /// <returns>The database, which deletes itself on disposal.</returns>
    public static async Task<TemporaryDatabase> CreateAsync()
    {
        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"kms-test-{Guid.CreateVersion7():N}.db");

        var services = new ServiceCollection();
        services
            .AddKeyManagementPersistence($"Data Source={filePath}")
            .AddKeyManagementUseCases()
            .AddKeyManagementTokens(new JwtOptions
            {
                // Test-only key. Long enough for HMAC-SHA256 and never used anywhere real.
                SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256",
            });

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>();
            await context.Database.MigrateAsync();
        }

        return new TemporaryDatabase(provider, filePath);
    }

    /// <summary>Opens a scope, which is how the server resolves a context.</summary>
    /// <returns>The scope. Dispose it when finished.</returns>
    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <summary>Runs work against a fresh context.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <returns>Whatever the work returned.</returns>
    public async Task<T> WithContextAsync<T>(Func<KeyManagementDbContext, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>();
        return await work(context);
    }

    /// <summary>Runs work against a fresh context.</summary>
    /// <param name="work">The work.</param>
    /// <returns>A task that completes when the work does.</returns>
    public async Task WithContextAsync(Func<KeyManagementDbContext, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>();
        await work(context);
    }

    /// <summary>Resolves a service the way the server would.</summary>
    /// <typeparam name="T">The service.</typeparam>
    /// <param name="scope">An open scope.</param>
    /// <returns>The service.</returns>
    public static T Resolve<T>(AsyncServiceScope scope)
        where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        // WAL mode leaves two sidecar files next to the database.
        foreach (var path in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A handle the pool has not released yet. The file is in the temp directory
                // and failing a passing test over cleanup would be the worse outcome.
            }
        }
    }
}
