using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// The SQLite settings the schema depends on, checked against a real connection rather than
/// trusted because the interceptor exists.
/// </summary>
public sealed class SqliteConfigurationTests
{
    [Fact]
    public async Task Foreign_keys_are_enforced_on_every_connection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var enabled = await database.WithContextAsync(async context =>
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(), provider: null);
        });

        Assert.Equal(1, enabled);
    }

    [Fact]
    public async Task A_foreign_key_violation_is_actually_rejected()
    {
        // The pragma reporting 1 is not the same as the constraint doing anything. This is
        // the test that would fail if the interceptor stopped running.
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.WithContextAsync(async context =>
        {
            var orphan = new Asset("PR-001", "Points at no group", AssetGroupId.New());
            context.Assets.Add(orphan);

            var error = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());

            var sqlite = Assert.IsType<SqliteException>(error.InnerException);
            Assert.Contains("FOREIGN KEY", sqlite.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task The_journal_is_in_write_ahead_mode()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var mode = await database.WithContextAsync(async context =>
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return (string?)await command.ExecuteScalarAsync();
        });

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task A_blocked_writer_waits_rather_than_failing_immediately()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var timeout = await database.WithContextAsync(async context =>
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(), provider: null);
        });

        Assert.Equal(
            (long)PersistenceServiceCollectionExtensions.DefaultBusyTimeout.TotalMilliseconds,
            timeout);
    }

    [Fact]
    public async Task Every_expected_table_exists_after_migrating()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var tables = await database.WithContextAsync(async context =>
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

        string[] expected =
        [
            "Users", "Roles", "UserRoles", "AssetGroupMemberships", "RefreshTokens",
            "Assets", "AssetGroups", "Cabinets", "Slots", "Checkouts",
            "AuditEvents", "DeviceEvents",
        ];

        Assert.All(expected, table => Assert.Contains(table, tables));
    }
}
