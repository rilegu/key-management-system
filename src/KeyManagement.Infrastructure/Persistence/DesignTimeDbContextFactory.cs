using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Scaffolding a migration only needs the model, never a real database, so the connection
/// string here points at a throwaway file and is never opened. Without this the tool would
/// have to start the server to find a context.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KeyManagementDbContext>
{
    /// <inheritdoc />
    public KeyManagementDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KeyManagementDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new KeyManagementDbContext(options);
    }
}
