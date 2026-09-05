namespace XE_Local_AI_Engine.Tests.Persistence;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the node SQLite connection posture — WAL journaling, a native busy_timeout, and synchronous=NORMAL
///     — is applied on both the raw-ADO open path and the EF connection interceptor, that an existing non-WAL database
///     converts safely, and that busy_timeout lets a second writer wait rather than fail instantly.
/// </summary>
[NotInParallel]
public sealed class NodeSqlitePragmasTests : IDisposable
{
    /// <summary>Past the waiter's 1 s command timeout, well under the 5 s busy_timeout under test.</summary>
    private static readonly TimeSpan LockHoldPastCommandTimeout = TimeSpan.FromMilliseconds(1300);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public NodeSqlitePragmasTests()
    {
        Directory.CreateDirectory(_dir);
        // The static raw-open helper reads the process-wide settings; pin the production defaults for these tests.
        NodeSqlitePragmas.Configure(NodeSqlitePragmaSettings.Default);
    }

    public void Dispose()
    {
        // Release pooled SQLite handles so the temp WAL/-shm files can be removed.
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a lingering handle on a CI runner must not fail the test.
        }
    }

    [Test]
    public async Task OpenAndConfigureAsync_AppliesWalBusyTimeoutAndSynchronous_AndDatabaseIsWritable()
    {
        var path = Path.Combine(_dir, "posture.sqlite");
        await using var connection = new SqliteConnection($"Data Source={path}");

        await NodeSqlitePragmas.OpenAndConfigureAsync(connection, CancellationToken.None);

        AssertEx.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        AssertEx.Equal(expected: 5000L, await ScalarAsync<long>(connection, "PRAGMA busy_timeout;"));
        AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "PRAGMA synchronous;")); // 1 == NORMAL

        // Prove real writability — BEGIN IMMEDIATE alone does not (agent-knowledge): actually create and read a row.
        await ExecuteAsync(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        await ExecuteAsync(connection, "INSERT INTO t(v) VALUES('written');");
        AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t;"));
    }

    [Test]
    public async Task ExistingNonWalDatabase_ConvertsToWalSafely_AndPreservesData()
    {
        var path = Path.Combine(_dir, "legacy.sqlite");

        // Seed a database in the default (non-WAL) journal mode with a row.
        await using (var seed = new SqliteConnection($"Data Source={path}"))
        {
            await seed.OpenAsync();
            AssertEx.True(!string.Equals(await ScalarAsync<string>(seed, "PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase),
                "Precondition: the seed database must not already be in WAL mode.");
            await ExecuteAsync(seed, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
            await ExecuteAsync(seed, "INSERT INTO t(v) VALUES('kept');");
        }

        SqliteConnection.ClearAllPools();

        // Reopen through the init path: it must convert the file to WAL, keep the existing row, and stay writable.
        await using var connection = new SqliteConnection($"Data Source={path}");
        await NodeSqlitePragmas.OpenAndConfigureAsync(connection, CancellationToken.None);

        AssertEx.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        AssertEx.Equal("kept", await ScalarAsync<string>(connection, "SELECT v FROM t WHERE id = 1;"));
        await ExecuteAsync(connection, "INSERT INTO t(v) VALUES('added-after-conversion');");
        AssertEx.Equal(expected: 2L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t;"));
    }

    [Test]
    public async Task ConnectionInterceptor_AppliesPragmasWhenEfOpensTheConnection()
    {
        var path = Path.Combine(_dir, "ef-interceptor.sqlite");
        var options = new DbContextOptionsBuilder<ProbeContext>()
                      .UseSqlite($"Data Source={path}")
                      .AddInterceptors(new NodeSqliteConnectionInterceptor(NodeSqlitePragmaSettings.Default, NullLogger<NodeSqliteConnectionInterceptor>.Instance))
                      // Fresh per-test options create a new EF internal service provider; in a FULL-SUITE run the
                      // process-wide count crosses EF's 20-provider threshold and the warning (an error in this solution)
                      // throws. The established repo-wide test pattern is to ignore it on throwaway options.
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        await using var context = new ProbeContext(options);
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();

        AssertEx.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        AssertEx.Equal(expected: 5000L, await ScalarAsync<long>(connection, "PRAGMA busy_timeout;"));

        await context.Database.CloseConnectionAsync();
    }

    [Test]
    public async Task BusyTimeout_LetsSecondWriterWaitAndSucceed_RatherThanFailInstantly()
    {
        var path = Path.Combine(_dir, "contend.sqlite");
        var settings = new NodeSqlitePragmaSettings(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 5000, NodeSqliteSynchronousMode.Normal);

        await using var holder = await OpenConfiguredAsync(path, settings);
        await ExecuteAsync(holder, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");

        await using var writer = await OpenConfiguredAsync(path, settings);

        // The holder takes the single WAL write lock (a deferred transaction upgrades on its first write).
        await using var holderTransaction = (SqliteTransaction)await holder.BeginTransactionAsync(CancellationToken.None);
        await ExecuteAsync(holder, "INSERT INTO t(v) VALUES('holder');", holderTransaction);

        // The second writer's INSERT must block on the write lock (via busy_timeout) rather than throw SQLITE_BUSY. Its
        // command timeout is capped at 1s so success past that window proves the native busy_timeout carried the wait,
        // not Microsoft.Data.Sqlite's own command-level retry.
        var writerInsert = Task.Run(async () =>
        {
            await using var command = writer.CreateCommand();
            command.CommandText = "INSERT INTO t(v) VALUES('waiter');";
            command.CommandTimeout = 1;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        });

        // real-timer: the subject IS native SQLite's busy_timeout, measured by SQLite's own clock. Holding the lock
        // past the writer's 1 s command timeout but well under the 5 s busy_timeout is what distinguishes the native
        // wait from Microsoft.Data.Sqlite's command-level retry; no injected TimeProvider reaches either.
        await Task.Delay(LockHoldPastCommandTimeout);
        await holderTransaction.CommitAsync(CancellationToken.None);

        await writerInsert; // must not throw
        AssertEx.Equal(expected: 2L, await ScalarAsync<long>(holder, "SELECT COUNT(*) FROM t;"));
    }

    [Test]
    public async Task SharedCacheConnection_SkipsWalWithoutLoggingAWarning()
    {
        // Reproduces the Aspire-dev connection posture: the CommunityToolkit Sqlite integration hands EF a
        // "Data Source=…;Cache=Shared;Mode=ReadWriteCreate" string. Several services open the node database
        // concurrently at startup, so applying PRAGMA journal_mode=WAL on a shared-cache handle is refused
        // (SQLite error 6/8) and used to log 'could not apply PRAGMA journal_mode' on every first run.
        var path = Path.Combine(_dir, "shared-cache.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var logger = new WarningCapturingLogger();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await NodeSqlitePragmas.ApplyAsync(connection, NodeSqlitePragmaSettings.Default, logger, CancellationToken.None);

        AssertEx.Empty(logger.Warnings, "A shared-cache connection must skip WAL rather than log a PRAGMA warning.");
        // Proves the guard took the skip branch: WAL was never attempted, so the DB stays in its default journal mode.
        AssertEx.True(!string.Equals(await ScalarAsync<string>(connection, "PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase),
            "WAL must be skipped on a shared-cache connection.");
    }

    private static async Task<SqliteConnection> OpenConfiguredAsync(string path, NodeSqlitePragmaSettings settings)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await NodeSqlitePragmas.ApplyAsync(connection, settings, logger: null, CancellationToken.None);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed pragma/SQL text — never user input.")]
    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed SQL text — never user input.")]
    private static async Task ExecuteAsync(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync();
    }

    // A minimal EF context used only to force an EF-initiated connection open through the interceptor.
    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options);

    // Captures Warning-level log entries so a test can assert the pragma path stayed quiet.
    private sealed class WarningCapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
