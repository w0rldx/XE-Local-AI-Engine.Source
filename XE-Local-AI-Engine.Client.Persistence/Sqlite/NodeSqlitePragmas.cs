namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

/// <summary>
///     Applies the node SQLite pragmas (busy_timeout, foreign_keys, WAL, synchronous) on open. EF's interceptor and
///     the raw-ADO helpers both route here; re-applying per open is idempotent, so a pooled handle is never left
///     unconfigured.
/// </summary>
/// <remarks>
///     WAL is a file-level property, so once any connection sets it the whole database file runs under WAL — the
///     shared Quartz job store's connections included. The three tuning pragmas degrade with a warning;
///     <c>foreign_keys</c> fails the open instead, because this is the layer that enforces it on connection strings
///     the process did not build. Callers that own their connection dispose it and retry; EF's interceptor fails the
///     query that triggered the open.
/// </remarks>
public static class NodeSqlitePragmas
{
    // Process-wide default consumed by the static raw-open helpers, which cannot take injected options. Swapped once at the composition root via Configure; a
    // volatile reference read/write is atomic. Defaults to the production values, so an unconfigured host (tests, design-time) still gets WAL + busy_timeout.
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
        await ApplyAsync(connection, _settings, NodeSqliteDiagnostics.Logger, cancellationToken);
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

        Execute(logger, connection, required: false, "busy_timeout", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = BusyTimeoutSql(settings);
            command.ExecuteNonQuery();
        });

        Execute(logger, connection, required: true, "foreign_keys", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = ForeignKeysSql;
            command.ExecuteNonQuery();
        });

        if (!ShouldApplyWal(connection, settings))
        {
            return;
        }

        Execute(logger, connection, required: false, "journal_mode", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = command.ExecuteScalar() as string;
            WarnIfNotWal(logger, mode);
        });

        Execute(logger, connection, required: false, "synchronous", () =>
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

        await ExecuteAsync(logger, connection, required: false, "busy_timeout", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BusyTimeoutSql(settings);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });

        await ExecuteAsync(logger, connection, required: true, "foreign_keys", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ForeignKeysSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        });

        if (!ShouldApplyWal(connection, settings))
        {
            return;
        }

        await ExecuteAsync(logger, connection, required: false, "journal_mode", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken) as string;
            WarnIfNotWal(logger, mode);
        });

        await ExecuteAsync(logger, connection, required: false, "synchronous", async () =>
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

    /// <summary>
    ///     Returns <paramref name="connectionString" /> with a shared cache on an on-disk database switched to the private
    ///     default; any other string is returned unchanged.
    /// </summary>
    /// <remarks>
    ///     On a shared cache SQLite locks per table and answers a conflict with SQLITE_LOCKED_SHAREDCACHE (262), which
    ///     busy_timeout never waits on: one connection's uncommitted schema change fails every other connection's next
    ///     prepare outright, the first pragma of a fresh open included. A private cache under WAL waits instead. A named
    ///     in-memory database keeps its shared cache, since that is the only way two connections see one.
    /// </remarks>
    public static string WithPrivateCache(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Cache != SqliteCacheMode.Shared
            || builder.Mode == SqliteOpenMode.Memory
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        _ = builder.Remove("Cache");
        return builder.ToString();
    }

    // WAL journaling is only safely settable on a writable, private-cache, on-disk connection, so the three shapes that cannot switch into it (in-memory,
    // read-only, shared cache) are skipped rather than warned about on every open. What each one reports or refuses: docs/wiki/08-data-and-persistence.md.
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
            // WAL is a persistent property that another connection sets once, so a later open that reads it back as wal is the norm. A non-wal result here means
            // the switch could not be applied (an exclusive lock held by another process, a read-only file); log and continue in the file's current journal mode.
            logger?.LogWarning("Node SQLite journal_mode is '{JournalMode}' after requesting WAL; continuing in the current mode.", mode ?? "unknown");
        }
    }

    // A required (enforcement) pragma never degrades: its failure fails the open, so the caller retries rather than getting an
    // unchecked connection. A tuning pragma degrades on SqliteException only; any other failure is named and rethrown.
    private static void Execute(ILogger? logger, DbConnection connection, bool required, string pragma, Action execute)
    {
        try
        {
            execute();
        }
        catch (SqliteException exception) when (!required)
        {
            WarnDegraded(logger, connection, pragma, exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw TransientOpenFailure(logger, connection, pragma, exception);
        }
        catch (Exception exception)
        {
            LogApplyFailure(logger, connection, required, pragma, exception);
            throw;
        }
    }

    private static async Task ExecuteAsync(ILogger? logger, DbConnection connection, bool required, string pragma, Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (SqliteException exception) when (!required)
        {
            WarnDegraded(logger, connection, pragma, exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw TransientOpenFailure(logger, connection, pragma, exception);
        }
        catch (Exception exception)
        {
            LogApplyFailure(logger, connection, required, pragma, exception);
            throw;
        }
    }

    private static void LogApplyFailure(ILogger? logger, DbConnection connection, bool required, string pragma, Exception exception)
    {
        if (required)
        {
            logger?.LogError(exception, "Node SQLite could not apply PRAGMA {Pragma} (extended result code {ExtendedResultCode}); failing the connection open.",
                pragma,
                ExtendedResultCode(connection));
        }
        else
        {
            logger?.LogError(exception, "Node SQLite failed to apply PRAGMA {Pragma} (extended result code {ExtendedResultCode}); failing the connection open.",
                pragma,
                ExtendedResultCode(connection));
        }
    }

    private static void WarnDegraded(ILogger? logger, DbConnection connection, string pragma, SqliteException exception)
    {
        logger?.LogWarning(exception, "Node SQLite could not apply PRAGMA {Pragma} (extended result code {ExtendedResultCode}); continuing without it.",
            pragma,
            ExtendedResultCode(connection));
    }

    // The pragma text is fixed, so this is the driver slicing it by a tail pointer a failed native prepare never wrote
    // (MISUSE or NOMEM), not a caller bug: fail the open as a type a retry loop logs at Warning (NodeSqliteTransientOpenException).
    private static NodeSqliteTransientOpenException TransientOpenFailure(ILogger? logger, DbConnection connection, string pragma, ArgumentOutOfRangeException exception)
    {
        var extendedResultCode = ExtendedResultCode(connection);
        logger?.LogWarning(exception, "Node SQLite could not prepare PRAGMA {Pragma} (extended result code {ExtendedResultCode}); failing the connection open as transient.",
            pragma,
            extendedResultCode);
        return new NodeSqliteTransientOpenException(
            string.Create(CultureInfo.InvariantCulture, $"Node SQLite could not prepare PRAGMA {pragma} on a freshly opened connection (extended result code {extendedResultCode})."),
            exception);
    }

    // What the handle last reported (MISUSE 21 vs NOMEM 7 vs BUSY 5...), read only to decorate a failure already being
    // reported; null for a connection that is not an open Microsoft.Data.Sqlite handle.
    private static int? ExtendedResultCode(DbConnection connection)
    {
        if (connection is not SqliteConnection { Handle: { IsInvalid: false, IsClosed: false } handle })
        {
            return null;
        }

        try
        {
            return raw.sqlite3_extended_errcode(handle);
        }
        catch (ObjectDisposedException)
        {
            // Closed between the check and the call.
            return null;
        }
    }
}
