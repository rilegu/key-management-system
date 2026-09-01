using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// Applies the SQLite settings this schema depends on, to every connection.
/// </summary>
/// <remarks>
/// <para>
/// Per connection, not once at startup, because connection pooling hands out fresh handles and
/// two of these settings reset with them.
/// </para>
/// <para>
/// <c>foreign_keys</c> is the dangerous one: SQLite ignores foreign key constraints unless
/// each connection asks for them, silently and with no error, so a schema full of correct
/// declarations enforces nothing without this.
/// </para>
/// </remarks>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private readonly TimeSpan _busyTimeout;

    /// <summary>Creates the interceptor.</summary>
    /// <param name="busyTimeout">How long a blocked writer waits before giving up.</param>
    public SqlitePragmaInterceptor(TimeSpan busyTimeout) => _busyTimeout = busyTimeout;

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildPragmas(_busyTimeout);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildPragmas(TimeSpan busyTimeout) =>
        string.Format(
            CultureInfo.InvariantCulture,
            // WAL: readers proceed while the gateway or the API writes.
            // busy_timeout: SQLite takes one writer at a time, so a second one waits.
            // synchronous NORMAL: safe against process crash under WAL, and much faster than FULL.
            "PRAGMA foreign_keys = ON;" +
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA busy_timeout = {0};" +
            "PRAGMA synchronous = NORMAL;",
            (int)busyTimeout.TotalMilliseconds);

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = BuildPragmas(_busyTimeout);
        command.ExecuteNonQuery();
    }
}
