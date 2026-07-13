namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddChatMaintenanceState</c> migration: it creates the <c>chat_maintenance_state</c> durable
///     key/value table (name PK + value, both NOT NULL, unencrypted) on both an upgrade from the immediately preceding
///     migration and a fresh migrate-to-head, drops it on rollback, and leaves no model/snapshot drift.
/// </summary>
public sealed class AddChatMaintenanceStateMigrationTests : IDisposable
{
    private const string PreChatMaintenanceStateMigrationId = "20260713170221_RepairAndUniqueMessageSequence";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_CreatesChatMaintenanceStateTable()
    {
        var databasePath = GetDatabasePath("chat-maintenance-state-up.sqlite");

        // Bring the schema up to exactly the migration before this one, then apply the rest — so this migration's Up is
        // exercised as an in-place upgrade of an existing database, not just a fresh create.
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreChatMaintenanceStateMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "chat_maintenance_state").ConfigureAwait(false),
            "Migration should create the chat_maintenance_state table.");

        var columns = await GetColumnInfoAsync(connection).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, columns.Count, "chat_maintenance_state should expose exactly two columns.");
        AssertEx.True(columns.ContainsKey("name") && columns.ContainsKey("value"), "chat_maintenance_state should expose the name + value columns.");
        AssertEx.True(columns["name"].NotNull, "chat_maintenance_state.name must be NOT NULL.");
        AssertEx.True(columns["value"].NotNull, "chat_maintenance_state.value must be NOT NULL.");
        AssertEx.True(columns["name"].IsPrimaryKey, "chat_maintenance_state.name must be the primary key.");
        AssertEx.False(columns["value"].IsPrimaryKey, "chat_maintenance_state.value must not be part of the primary key.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_CreatesChatMaintenanceStateTable()
    {
        var databasePath = GetDatabasePath("chat-maintenance-state-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.True(await TableExistsAsync(connection, "chat_maintenance_state").ConfigureAwait(false),
            "A fresh migrate-to-head should create the chat_maintenance_state table.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsChatMaintenanceStateTable()
    {
        var databasePath = GetDatabasePath("chat-maintenance-state-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreChatMaintenanceStateMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.False(await TableExistsAsync(connection, "chat_maintenance_state").ConfigureAwait(false),
            "Rolling back one migration should drop the chat_maintenance_state table.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        // In-process snapshot-drift guard: the runtime model and the latest migration's snapshot must agree, so a future
        // entity change without a regenerated migration fails here rather than only on a real DB.
        var databasePath = GetDatabasePath("chat-maintenance-state-drift.sqlite");
        await using var context = CreateContext(databasePath);

        AssertEx.False(context.Database.HasPendingModelChanges(),
            "The NodeChat model has drifted from the latest migration snapshot — regenerate the migration.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<IReadOnlyDictionary<string, (bool NotNull, bool IsPrimaryKey)>> GetColumnInfoAsync(SqliteConnection connection)
    {
        // PRAGMA table_info exposes each column's NOT NULL flag (notnull) and primary-key position (pk, 0 = not part of
        // the PK). The table name is a fixed literal, so this stays free of caller-supplied SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(chat_maintenance_state);";

        var columns = new Dictionary<string, (bool NotNull, bool IsPrimaryKey)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            var notNull = reader.GetInt64(reader.GetOrdinal("notnull")) != 0L;
            var isPrimaryKey = reader.GetInt64(reader.GetOrdinal("pk")) != 0L;
            columns[name] = (notNull, isPrimaryKey);
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
