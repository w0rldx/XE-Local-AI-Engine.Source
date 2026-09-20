namespace XE_Local_AI_Engine.Tests.Persistence;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the node SQLite posture — WAL, busy_timeout, synchronous=NORMAL, foreign keys — on both the raw-ADO
///     and EF interceptor open paths, that an existing non-WAL database converts, and that a second writer waits.
/// </summary>
[NotInParallel]
[Category(TestCategories.Integration)]
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
        var settings = new NodeSqlitePragmaSettings { EnableWriteAheadLog = true, BusyTimeoutMilliseconds = 5000, Synchronous = NodeSqliteSynchronousMode.Normal };

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

    /// <summary>
    ///     Layer one: the bootstrap's connection string must SAY Foreign Keys=True. Parsed, not opened — the bundled
    ///     e_sqlite3 defaults enforcement on, so an open stays green even after the setting is dropped.
    /// </summary>
    [Test]
    public void DesktopBootstrapConnectionString_StatesForeignKeysOn()
    {
        using var configuration = new ConfigurationManager();
        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, _ => _dir);

        var connectionString = AssertEx.NotNull(configuration.GetConnectionString("node-sqlite"));
        var builder = new SqliteConnectionStringBuilder(connectionString);

        AssertEx.Equal(expected: true, builder.ForeignKeys,
            "The node's connection string must state Foreign Keys=True, not inherit it from the SQLite build.");
    }

    /// <summary>
    ///     Layer two: the applier must TURN enforcement on, not merely find it on. Both connections start from
    ///     Foreign Keys=False, so only the pragma can flip them; the sync path EF uses is covered as well as the async.
    /// </summary>
    [Test]
    public async Task PragmaApplier_TurnsForeignKeysOn_OnAConnectionThatStartedOff()
    {
        var asyncPath = Path.Combine(_dir, "fk-off-async.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={asyncPath};Foreign Keys=False"))
        {
            await NodeSqlitePragmas.OpenAndConfigureAsync(connection, CancellationToken.None);
            AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"),
                "The async open path must enable enforcement on a connection that asked for it off.");
        }

        var syncPath = Path.Combine(_dir, "fk-off-sync.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={syncPath};Foreign Keys=False"))
        {
            await connection.OpenAsync();
            AssertEx.Equal(expected: 0L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"),
                "Precondition: the connection must really start with enforcement off, or this proves nothing.");

            NodeSqlitePragmas.Apply(connection, NodeSqlitePragmaSettings.Default, logger: null);

            AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"),
                "The synchronous apply path — the one EF's ConnectionOpened uses — must enable enforcement too.");
        }
    }

    [Test]
    public async Task ProductionConnectionPath_EnforcesForeignKeys_SoADeclaredCascadeFires()
    {
        // Wired the way production is — DesktopBootstrap's own string plus NodeSqliteConnectionInterceptor, not a
        // string re-typed here — so a declared cascade keeps firing even if the bundled SQLite build stops defaulting on.
        using var configuration = new ConfigurationManager();
        // The resolver stands in for %LOCALAPPDATA%, so the bootstrap builds its data directory under this test's own
        // temp root instead of the real per-user one.
        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, _ => _dir);
        var connectionString = AssertEx.NotNull(configuration.GetConnectionString("node-sqlite"));

        var options = new DbContextOptionsBuilder<ProbeContext>()
                      .UseSqlite(connectionString)
                      .AddInterceptors(new NodeSqliteConnectionInterceptor(NodeSqlitePragmaSettings.Default, NullLogger<NodeSqliteConnectionInterceptor>.Instance))
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        await using var context = new ProbeContext(options);
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();

        AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));

        // The behavioural consequence, once: a declared cascade really removes the child rows.
        await ExecuteAsync(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        await ExecuteAsync(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id) ON DELETE CASCADE);");
        await ExecuteAsync(connection, "INSERT INTO parent(id) VALUES(1), (2);");
        await ExecuteAsync(connection, "INSERT INTO child(id, parent_id) VALUES(10, 1), (11, 2);");

        await ExecuteAsync(connection, "DELETE FROM parent WHERE id = 1;");

        // Unfiltered child total plus a surviving control row belonging to the other parent.
        AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM child;"));
        AssertEx.Equal(expected: 1L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM child WHERE parent_id = 2;"));

        await context.Database.CloseConnectionAsync();
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
    private sealed class ProbeContext : DbContext
    {
        public ProbeContext(DbContextOptions<ProbeContext> options) : base(options)
        {
        }
    }

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
