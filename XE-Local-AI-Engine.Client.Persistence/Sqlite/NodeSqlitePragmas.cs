namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
///     Applies the node SQLite connection pragmas (<c>busy_timeout</c>, WAL <c>journal_mode</c>, <c>synchronous</c>) to a
///     connection right after it opens. Two open mechanisms exist on the node database and both route through here:
///     <list type="bullet">
///         <item>EF-initiated opens (migrations, EF queries/saves, health probes) via <see cref="NodeSqliteConnectionInterceptor" />.</item>
///         <item>Raw-ADO opens on the EF context's <see cref="DbConnection" /> via the shared open-if-needed helpers.</item>
///     </list>
///     Applying on every physical open is idempotent and cheap, so it is correct regardless of Microsoft.Data.Sqlite's
///     connection pooling (a pooled handle keeps its pragma state, a fresh one gets it here). WAL is a persistent
///     file-level property, so once any connection sets it the whole database file — including the shared Quartz job
///     store's connections — runs under WAL.
/// </summary>
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

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(connection, _settings, logger: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the pragmas to an already-open connection (synchronous path — EF may open synchronously).</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PRAGMA text is composed only from a validated internal integer and fixed keywords — never user input; PRAGMAs do not accept bound parameters for these values.")]
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
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (!ShouldApplyWal(connection, settings))
        {
            return;
        }

        await TryExecuteAsync(logger, "journal_mode", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            WarnIfNotWal(logger, mode);
        }).ConfigureAwait(false);

        await TryExecuteAsync(logger, "synchronous", async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SynchronousSql(settings);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static string BusyTimeoutSql(NodeSqlitePragmaSettings settings)
    {
        return string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout={settings.BusyTimeoutMilliseconds};");
    }

    private static string SynchronousSql(NodeSqlitePragmaSettings settings)
    {
        return $"PRAGMA synchronous={settings.Synchronous.ToString().ToUpperInvariant()};";
    }

    // WAL and synchronous are only meaningful for an on-disk database. An in-memory database ignores/refuses WAL
    // ("PRAGMA journal_mode=WAL" returns "memory"), so skip both there rather than log a spurious warning every open.
    private static bool ShouldApplyWal(DbConnection connection, NodeSqlitePragmaSettings settings)
    {
        return settings.EnableWriteAheadLog && !IsMemoryDatabase(connection);
    }

    private static bool IsMemoryDatabase(DbConnection connection)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            return builder.Mode == SqliteOpenMode.Memory
                   || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // A non-SQLite or unparseable connection string: treat as on-disk and let the pragma itself no-op if unsupported.
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
            await execute().ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            logger?.LogWarning(exception, "Node SQLite could not apply PRAGMA {Pragma}; continuing without it.", pragma);
        }
    }
}
