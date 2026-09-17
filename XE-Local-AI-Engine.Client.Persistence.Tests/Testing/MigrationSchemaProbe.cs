namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;

/// <summary>
///     Migrates a throwaway SQLite file to some point in a context's migration chain and answers schema questions about
///     the result. The per-migration suites are one short assertion each on top of this, instead of each re-implementing
///     the same <c>sqlite_master</c>/<c>PRAGMA</c> plumbing.
///     <para>
///         Every query goes through SQLite's table-valued <c>pragma_*</c> functions rather than the <c>PRAGMA x(y)</c>
///         statement form, because only the former accepts a bound parameter — so a caller-supplied table name is never
///         concatenated into SQL.
///     </para>
/// </summary>
internal sealed class MigrationSchemaProbe : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly INodeSqliteKeyHolder _keyHolder;
    private readonly string _rootPath;

    private MigrationSchemaProbe(SqliteConnection connection, INodeSqliteKeyHolder keyHolder, string rootPath, string databasePath)
    {
        _connection = connection;
        _keyHolder = keyHolder;
        _rootPath = rootPath;
        _databasePath = databasePath;
    }

    /// <summary>
    ///     The throwaway database this probe migrated. A suite that has to reach the file through something other than
    ///     this probe — an application-layer reader that takes a <see cref="NodeChatDbContext" />, say — opens its own
    ///     context over this path rather than re-implementing the migrate-to-a-point plumbing.
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>Applies the whole <see cref="NodeChatDbContext" /> chain to an empty database.</summary>
    public static Task<MigrationSchemaProbe> MigrateChatAsync(string fileName)
    {
        return MigrateChatAsync(fileName, targetMigration: null);
    }

    /// <summary>
    ///     Applies the <see cref="NodeChatDbContext" /> chain up to <paramref name="targetMigration" /> (null = latest),
    ///     so a suite can observe the schema as it stood before a later migration changed it.
    /// </summary>
    public static Task<MigrationSchemaProbe> MigrateChatAsync(string fileName, string? targetMigration)
    {
        return CreateAsync(fileName, path => ApplyChatAsync(path, targetMigration));
    }

    /// <summary>
    ///     Applies the chat chain further, up to <paramref name="targetMigration" /> (null = latest), over whatever this
    ///     probe's database already holds. With <see cref="ExecuteAsync" /> this is how a data-bearing migration is
    ///     tested: migrate to its predecessor, seed the historical rows, then run exactly that migration over them.
    /// </summary>
    public async Task MigrateToAsync(string? targetMigration)
    {
        // The migration runs on its own connection; ours is closed across it so no reader holds a lock during the DDL.
        await _connection.CloseAsync();
        await ApplyChatAsync(_databasePath, _keyHolder, targetMigration);
        await _connection.OpenAsync();
    }

    /// <summary>Applies the whole <see cref="NodeIdentityDbContext" /> chain to an empty database.</summary>
    public static Task<MigrationSchemaProbe> MigrateIdentityAsync(string fileName)
    {
        return CreateAsync(fileName, ApplyIdentityAsync);
    }

    /// <summary>
    ///     A probe over a copy of the shared at-head chat template instead of a from-scratch chain replay. Use this
    ///     wherever the suite only needs a database that is <em>already</em> at head to inspect or mutate; keep
    ///     <see cref="MigrateChatAsync(string)" /> where the replay itself is the thing under test.
    /// </summary>
    public static Task<MigrationSchemaProbe> FromChatTemplateAsync(string fileName)
    {
        return CreateAsync(fileName, MigratedDatabaseTemplate.CopyChatHeadAsync);
    }

    /// <summary>
    ///     A probe over a copy of the shared template whose chain stops at <paramref name="targetMigration" />. The
    ///     migrations that follow it still run for real, through <see cref="MigrateToAsync" />.
    /// </summary>
    public static Task<MigrationSchemaProbe> FromChatTemplateAsync(string fileName, string targetMigration)
    {
        // Checked before anything is allocated, so the documented misuse — a target keyed on a file name or a GUID —
        // never gets as far as creating this probe's private directory.
        MigratedDatabaseTemplate.EnsureDeclaredChatMigration(targetMigration);

        return CreateAsync(fileName, path => MigratedDatabaseTemplate.CopyChatAtAsync(path, targetMigration));
    }

    /// <summary>The <see cref="FromChatTemplateAsync(string)" /> equivalent for the identity chain.</summary>
    public static Task<MigrationSchemaProbe> FromIdentityTemplateAsync(string fileName)
    {
        return CreateAsync(fileName, MigratedDatabaseTemplate.CopyIdentityHeadAsync);
    }

    /// <summary>
    ///     The one construction path: allocate the private directory and the key holder, let
    ///     <paramref name="prepareDatabaseAsync" /> produce the database at that path, then open it. Nothing owns the
    ///     directory or the holder until the probe exists, so a throw anywhere in between has to undo them here — no
    ///     <see cref="DisposeAsync" /> will ever run for a probe that was never returned.
    /// </summary>
    /// <param name="rootPath">
    ///     The private directory to create and, on failure, delete. Only a test that has to observe that deletion
    ///     passes its own; every other caller goes through the overload that allocates a fresh one.
    /// </param>
    internal static async Task<MigrationSchemaProbe> CreateAsync(string rootPath, string fileName, Func<string, Task> prepareDatabaseAsync)
    {
        ArgumentNullException.ThrowIfNull(prepareDatabaseAsync);

        var (databasePath, keyHolder) = Prepare(rootPath, fileName);

        try
        {
            await prepareDatabaseAsync(databasePath);

            // OpenAsync disposes its own connection when the open throws, so no half-open connection reaches here.
            return new MigrationSchemaProbe(await OpenAsync(databasePath), keyHolder, rootPath, databasePath);
        }
        catch
        {
            keyHolder.Dispose();

            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // The caller's exception says why the probe could not be built; a failure to delete a scratch
                // directory must not replace it.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reason as the IOException above: the original failure is the one worth reporting.
            }

            throw;
        }
    }

    private static Task<MigrationSchemaProbe> CreateAsync(string fileName, Func<string, Task> prepareDatabaseAsync)
    {
        return CreateAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), fileName, prepareDatabaseAsync);
    }

    public async ValueTask DisposeAsync()
    {
        // Scoped to this probe's connection string, not the process-global ClearAllPools: Microsoft.Data.Sqlite pools
        // per connection string, each probe uses a unique temp path, and the pooled connection keeps the file handle
        // that would otherwise make the delete below silently fail. ClearAllPools reaches every other test class's
        // pool as well — it does not close a connection another class is using, it stops that connection being reused
        // once it closes — so all it buys at parallel width is throwing away pools this probe has no business
        // touching. Cleared before the dispose so the connection string is read off a live object.
        SqliteConnection.ClearPool(_connection);
        await _connection.DisposeAsync();

        _keyHolder.Dispose();

        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    /// <summary>
    ///     Runs <paramref name="sql" /> against the probed database. This is the seed seam: the historical rows a
    ///     data-bearing migration has to convert cannot be written through the entity model, because the model describes
    ///     the schema as it is at head, not as it was when those rows were valid.
    /// </summary>
    public async Task ExecuteAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value goes in through `configure` as a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The first column of the first row of <paramref name="sql" />, with <c>DBNull</c> flattened to null.</summary>
    public async Task<object?> ScalarAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value goes in through `configure` as a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>Every value of the first column of <paramref name="sql" />, in row order, read as an integer.</summary>
    public async Task<IReadOnlyList<long>> LongsAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value goes in through `configure` as a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);

        var values = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt64(ordinal: 0));
        }

        return values;
    }

    /// <summary>
    ///     The <c>detail</c> line of every step SQLite's planner chose for <paramref name="sql" />. An index test needs
    ///     this and not just <see cref="IndexExistsAsync" />: that an index was created says nothing about whether the
    ///     planner picks it, and a wrong column order shows up here as a <c>SCAN</c> or a <c>USE TEMP B-TREE</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> QueryPlanAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value goes in through `configure` as a bound parameter.
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
#pragma warning restore CA2100

        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        var detail = reader.GetOrdinal("detail");
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(detail));
        }

        return steps;
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task<IReadOnlySet<string>> ColumnsAsync(string tableName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);
        return await ReadStringsAsync(command);
    }

    /// <summary>The declared default for <paramref name="columnName" />, exactly as SQLite recorded it (or null).</summary>
    public async Task<string?> ColumnDefaultAsync(string tableName, string columnName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT dflt_value FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", tableName);
        command.Parameters.AddWithValue("$column", columnName);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="indexName" /> exists on the table, has the expected uniqueness, and covers exactly <paramref name="columns" />.</summary>
    public async Task<bool> IndexExistsAsync(string tableName, string indexName, bool unique, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        await using var listCommand = _connection.CreateCommand();
        listCommand.CommandText = "SELECT \"unique\" FROM pragma_index_list($table) WHERE name = $index;";
        listCommand.Parameters.AddWithValue("$table", tableName);
        listCommand.Parameters.AddWithValue("$index", indexName);
        var uniqueFlag = await listCommand.ExecuteScalarAsync();
        if (uniqueFlag is null || Convert.ToInt64(uniqueFlag, CultureInfo.InvariantCulture) != (unique ? 1 : 0))
        {
            return false;
        }

        if (columns.Length == 0)
        {
            return true;
        }

        await using var infoCommand = _connection.CreateCommand();
        infoCommand.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno;";
        infoCommand.Parameters.AddWithValue("$index", indexName);

        var actual = new List<string>();
        await using var reader = await infoCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(ordinal: 0));
        }

        return actual.SequenceEqual(columns, StringComparer.Ordinal);
    }

    /// <summary>True when <paramref name="tableName" /> declares a foreign key on <paramref name="column" /> into <paramref name="principalTable" />.</summary>
    public async Task<bool> ForeignKeyExistsAsync(string tableName, string column, string principalTable)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_foreign_key_list($table) WHERE \"from\" = $column AND \"table\" = $principal;";
        command.Parameters.AddWithValue("$table", tableName);
        command.Parameters.AddWithValue("$column", column);
        command.Parameters.AddWithValue("$principal", principalTable);
        return await command.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    ///     The migration ids EF recorded as applied, which is what "the chain applied" means to it. The two history
    ///     tables are separate literals rather than a parameter, because a table name cannot be bound.
    /// </summary>
    public async Task<IReadOnlySet<string>> AppliedMigrationsAsync(bool identityContext)
    {
        await using var command = _connection.CreateCommand();
        if (identityContext)
        {
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory_Identity;";
        }
        else
        {
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
        }

        return await ReadStringsAsync(command);
    }

    private static async Task<IReadOnlySet<string>> ReadStringsAsync(SqliteCommand command)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(ordinal: 0));
        }

        return values;
    }

    /// <summary>
    ///     The hostless migrate-a-file primitive, for <see cref="MigratedDatabaseTemplate" /> to build its templates
    ///     through the same path the suites use. The key holder is irrelevant here — the migration context carries no
    ///     encryption interceptors — so this overload owns a throwaway one.
    /// </summary>
    internal static async Task ApplyChatAsync(string databasePath, string? targetMigration)
    {
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await ApplyChatAsync(databasePath, keyHolder, targetMigration);
    }

    /// <summary>The identity-chain half of <see cref="ApplyChatAsync(string, string?)" />, to head.</summary>
    internal static async Task ApplyIdentityAsync(string databasePath)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeIdentityDbContext>()
                      .UseSqlite($"Data Source={databasePath}",
                          static sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable))
                      .ConfigureWarnings(static warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        await using var context = new NodeIdentityDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static async Task ApplyChatAsync(string databasePath, INodeSqliteKeyHolder keyHolder, string? targetMigration)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder);

        if (targetMigration is null)
        {
            await context.Database.MigrateAsync();
        }
        else
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(targetMigration);
        }
    }

    private static (string DatabasePath, INodeSqliteKeyHolder KeyHolder) Prepare(string rootPath, string fileName)
    {
        _ = Directory.CreateDirectory(rootPath);
        return (Path.Combine(rootPath, fileName), new NullNodeSqliteKeyHolder());
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            // Nobody else holds this connection yet: a failed open would otherwise leave it — and the pool entry it
            // may already have taken — behind, and the pooled file handle is what makes the directory undeletable.
            SqliteConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }
}
