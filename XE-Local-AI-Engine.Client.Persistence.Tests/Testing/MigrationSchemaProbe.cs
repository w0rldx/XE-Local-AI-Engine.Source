namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

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

    /// <summary>Applies the whole <see cref="NodeChatDbContext" /> chain to an empty database.</summary>
    public static Task<MigrationSchemaProbe> MigrateChatAsync(string fileName)
    {
        return MigrateChatAsync(fileName, targetMigration: null);
    }

    /// <summary>
    ///     Applies the <see cref="NodeChatDbContext" /> chain up to <paramref name="targetMigration" /> (null = latest),
    ///     so a suite can observe the schema as it stood before a later migration changed it.
    /// </summary>
    public static async Task<MigrationSchemaProbe> MigrateChatAsync(string fileName, string? targetMigration)
    {
        var (databasePath, rootPath, keyHolder) = Prepare(fileName);

        await ApplyChatAsync(databasePath, keyHolder, targetMigration).ConfigureAwait(false);

        return new MigrationSchemaProbe(await OpenAsync(databasePath).ConfigureAwait(false), keyHolder, rootPath, databasePath);
    }

    /// <summary>
    ///     Applies the chat chain further, up to <paramref name="targetMigration" /> (null = latest), over whatever this
    ///     probe's database already holds. With <see cref="ExecuteAsync" /> this is how a data-bearing migration is
    ///     tested: migrate to its predecessor, seed the historical rows, then run exactly that migration over them.
    /// </summary>
    public async Task MigrateToAsync(string? targetMigration)
    {
        // The migration runs on its own connection; ours is closed across it so no reader holds a lock during the DDL.
        await _connection.CloseAsync().ConfigureAwait(false);
        await ApplyChatAsync(_databasePath, _keyHolder, targetMigration).ConfigureAwait(false);
        await _connection.OpenAsync().ConfigureAwait(false);
    }

    /// <summary>Applies the whole <see cref="NodeIdentityDbContext" /> chain to an empty database.</summary>
    public static async Task<MigrationSchemaProbe> MigrateIdentityAsync(string fileName)
    {
        var (databasePath, rootPath, keyHolder) = Prepare(fileName);

        var options = new DbContextOptionsBuilder<NodeIdentityDbContext>()
                      .UseSqlite($"Data Source={databasePath}",
                          static sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable))
                      .ConfigureWarnings(static warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        await using (var context = new NodeIdentityDbContext(options))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        return new MigrationSchemaProbe(await OpenAsync(databasePath).ConfigureAwait(false), keyHolder, rootPath, databasePath);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);

        // Microsoft.Data.Sqlite pools per connection string, and each probe uses a unique temp path; without this the
        // pooled connection keeps the file handle and the delete below silently fails.
        SqliteConnection.ClearAllPools();
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
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>The first column of the first row of <paramref name="sql" />, with <c>DBNull</c> flattened to null.</summary>
    public async Task<object?> ScalarAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value goes in through `configure` as a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
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
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetInt64(ordinal: 0));
        }

        return values;
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    public async Task<IReadOnlySet<string>> ColumnsAsync(string tableName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);
        return await ReadStringsAsync(command).ConfigureAwait(false);
    }

    /// <summary>The declared default for <paramref name="columnName" />, exactly as SQLite recorded it (or null).</summary>
    public async Task<string?> ColumnDefaultAsync(string tableName, string columnName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT dflt_value FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", tableName);
        command.Parameters.AddWithValue("$column", columnName);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="indexName" /> exists on the table, has the expected uniqueness, and covers exactly <paramref name="columns" />.</summary>
    public async Task<bool> IndexExistsAsync(string tableName, string indexName, bool unique, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        await using var listCommand = _connection.CreateCommand();
        listCommand.CommandText = "SELECT \"unique\" FROM pragma_index_list($table) WHERE name = $index;";
        listCommand.Parameters.AddWithValue("$table", tableName);
        listCommand.Parameters.AddWithValue("$index", indexName);
        var uniqueFlag = await listCommand.ExecuteScalarAsync().ConfigureAwait(false);
        if (uniqueFlag is null || Convert.ToInt64(uniqueFlag, System.Globalization.CultureInfo.InvariantCulture) != (unique ? 1 : 0))
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
        await using var reader = await infoCommand.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
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

        return await ReadStringsAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> ReadStringsAsync(SqliteCommand command)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetString(ordinal: 0));
        }

        return values;
    }

    private static async Task ApplyChatAsync(string databasePath, INodeSqliteKeyHolder keyHolder, string? targetMigration)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder);

        if (targetMigration is null)
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
        else
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(targetMigration).ConfigureAwait(false);
        }
    }

    private static (string DatabasePath, string RootPath, INodeSqliteKeyHolder KeyHolder) Prepare(string fileName)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return (Path.Combine(rootPath, fileName), rootPath, new NullNodeSqliteKeyHolder());
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }
}
