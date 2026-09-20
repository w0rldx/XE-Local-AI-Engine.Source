namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
///     Applies the node SQLite pragmas (busy_timeout, foreign_keys, WAL, synchronous) on open. EF's interceptor and
///     the raw-ADO helpers both route here; re-applying per open is idempotent, so a pooled handle is never left
///     unconfigured.
/// </summary>
/// <remarks>
///     WAL is a file-level property, so once any connection sets it the whole database file runs under WAL — the
///     shared Quartz job store's connections included.
/// </remarks>
public static class NodeSqlitePragmas
{
    // Process-wide default consumed by the static raw-open helpers (which cannot take injected options). Swapped once at
    // the composition root via Configure; volatile reference read/write is atomic. Defaults to the production values, so
    // an unconfigured host (tests, design-time) still gets WAL + busy_timeout.
    private static volatile NodeSqlitePragmaSettings _settings = NodeSqlitePragmaSettings.Default;

    /// <summary>The effective process-wide settings used by <see cref="OpenAndConfigureAsync" />.</summary>
    public static NodeSqlitePragmaSettings Settings => _settings;

    /// <summary>Sets the process-wide settings for the static raw-open helpers. Called once at the composition root.</summary>
    public static void Configure(NodeSqlitePragmaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    ///     Opens <paramref name="connection" /> if it is not already open and applies the process-wide pragmas when this
    ///     call performed the open (a connection already open was configured by whoever opened it). The shared raw-ADO
    ///     open helper for the node database.
    /// </summary>
    public static async Task OpenAndConfigureAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("The node chat database connection was not available.");
        }

        if (connection.State == ConnectionState.Open)
        {
            return;
        }

        await connection.OpenAsync(cancellationToken);
        await ApplyAsync(connection, _settings, logger: null, cancellationToken);
    }

    /// <summary>Applies the pragmas to an already-open connection (synchronous path — EF may open synchronously).</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PRAGMA text is composed only from a validated internal integer and fixed keywords — never user input; PRAGMAs do not accept bound parameters for these values.")]
    // Forced sync: this is the documented sync twin of ApplyAsync, called from EF Core's synchronous
    // DbConnectionInterceptor.ConnectionOpened (NodeSqliteConnectionInterceptor) which has no async shape.
#pragma warning disable MA0045
    public static void Apply(DbConnection connection, NodeSqlitePragmaSettings settings, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        TryExecute(logger, "busy_timeout", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = BusyTimeoutSql(settings);
            command.ExecuteNonQuery();
        });

        TryExecute(logger, "foreign_keys", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = ForeignKeysSql;
            command.ExecuteNonQuery();
        });

        if (!ShouldApplyWal(connection, settings))
        {
            return;
        }

        TryExecute(logger, "journal_mode", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = command.ExecuteScalar() as string;
            WarnIfNotWal(logger, mode);
        });

        TryExecute(logger, "synchronous", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SynchronousSql(settings);
            command.ExecuteNonQuery();
        });
    }
#pragma warning restore MA0045

    /// <summary>Applies the pragmas to an already-open connection (async path).</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PRAGMA text is composed only from a validated internal integer and fixed keywords — never user input; PRAGMAs do not accept bound parameters for these values.")]
    public static async Task ApplyAsync(DbConnection connection, NodeSqlitePragmaSettings settings, ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        await TryExecuteAsync(logger, "busy_timeout", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BusyTimeoutSql(settings);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });

        await TryExecuteAsync(logger, "foreign_keys", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ForeignKeysSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        });

        if (!ShouldApplyWal(connection, settings))
        {
            return;
        }

        await TryExecuteAsync(logger, "journal_mode", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken) as string;
            WarnIfNotWal(logger, mode);
        });

        await TryExecuteAsync(logger, "synchronous", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SynchronousSql(settings);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    // Emitted before the WAL guard, so it reaches the connection shapes that cannot switch into WAL too. The bundled
    // e_sqlite3 already defaults enforcement on; stating it keeps the declared cascades off the native build's mercy.
    private const string ForeignKeysSql = "PRAGMA foreign_keys=ON;";

    private static string BusyTimeoutSql(NodeSqlitePragmaSettings settings)
    {
        return string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout={settings.BusyTimeoutMilliseconds};");
    }

    private static string SynchronousSql(NodeSqlitePragmaSettings settings)
    {
        return $"PRAGMA synchronous={settings.Synchronous.ToString().ToUpperInvariant()};";
    }

    // WAL journaling is only safely settable on a writable, private-cache, on-disk connection. Skip it (rather than log a
    // spurious warning on every open) for the three connection shapes that cannot switch into WAL. An in-memory database
    // reports its journal mode as memory and never wal. A read-only connection refuses the write with SQLite error 8. A
    // shared-cache connection (the Aspire dev integration sets one) collides with the sibling connections that other
    // services open against the node database concurrently at startup, so the switch is refused with SQLite error 6 or 8.
    // The desktop and packaged builds use a plain private-cache data source, so they still get WAL.
    private static bool ShouldApplyWal(DbConnection connection, NodeSqlitePragmaSettings settings)
    {
        return settings.EnableWriteAheadLog && !IsWalIncompatibleConnection(connection);
    }

    private static bool IsWalIncompatibleConnection(DbConnection connection)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            return builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly
                   || builder.Cache == SqliteCacheMode.Shared
                   || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // A non-SQLite or unparseable connection string: treat as a writable private-cache file and let the pragma itself no-op if unsupported.
            return false;
        }
    }

    private static void WarnIfNotWal(ILogger? logger, string? mode)
    {
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            // WAL is a persistent property that another connection sets once, so a later open that reads it back as wal is
            // the norm. A non-wal result here means the switch could not be applied (e.g. an exclusive lock held by
            // another process, or a read-only file); log and continue in whatever journal mode the file already has.
            logger?.LogWarning("Node SQLite journal_mode is '{JournalMode}' after requesting WAL; continuing in the current mode.", mode ?? "unknown");
        }
    }

    private static void TryExecute(ILogger? logger, string pragma, Action execute)
    {
        try
        {
            execute();
        }
        catch (SqliteException exception)
        {
            logger?.LogWarning(exception, "Node SQLite could not apply PRAGMA {Pragma}; continuing without it.", pragma);
        }
    }

    private static async Task TryExecuteAsync(ILogger? logger, string pragma, Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (SqliteException exception)
        {
            logger?.LogWarning(exception, "Node SQLite could not apply PRAGMA {Pragma}; continuing without it.", pragma);
        }
    }
}
