namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AddQuartzPreferredNodeColumnsMigrationTests : IDisposable
{
    private const string PreviousMigrationId = "20261008091329_AddKnowledgeIngestionAttempts";
    private const string PreferredNodeMigrationId = "20261012173207_AddQuartzPreferredNodeColumns";

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
    public async Task MigrateAsync_FromPreviousMigration_AddsBothPreferredNodeColumns()
    {
        var databasePath = GetDatabasePath("preferred-node-up.sqlite");

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreviousMigrationId);

        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            var before = await GetTriggerColumnsAsync(connection);
            AssertEx.False(before.ContainsKey("PREFERRED_NODE") || before.ContainsKey("PREFERRED_NODE_AUTO"),
                "QRTZ_TRIGGERS should not have the preferred-node columns before the migration.");
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreferredNodeMigrationId);
        }

        await using var migrated = await OpenConnectionAsync(databasePath);
        var after = await GetTriggerColumnsAsync(migrated);

        AssertEx.True(after.TryGetValue("PREFERRED_NODE", out var preferredNode) && !preferredNode.NotNull,
            "Migration should add a nullable PREFERRED_NODE column to QRTZ_TRIGGERS.");
        AssertEx.True(after.TryGetValue("PREFERRED_NODE_AUTO", out var preferredNodeAuto)
                      && preferredNodeAuto.NotNull && preferredNodeAuto.DefaultValue == "0",
            "Migration should add PREFERRED_NODE_AUTO NOT NULL DEFAULT 0 to QRTZ_TRIGGERS.");
    }

    [Test]
    public async Task MigrateAsync_AtHead_QrtzTriggersHasBothPreferredNodeColumns()
    {
        var databasePath = GetDatabasePath("preferred-node-head.sqlite");

        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        await using var connection = await OpenConnectionAsync(databasePath);
        var columns = await GetTriggerColumnsAsync(connection);

        AssertEx.True(columns.ContainsKey("PREFERRED_NODE") && columns.ContainsKey("PREFERRED_NODE_AUTO"),
            "The head schema should expose both Quartz preferred-node columns on QRTZ_TRIGGERS.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsBothPreferredNodeColumns()
    {
        var databasePath = GetDatabasePath("preferred-node-down.sqlite");

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreferredNodeMigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        var columns = await GetTriggerColumnsAsync(connection);

        AssertEx.False(columns.ContainsKey("PREFERRED_NODE") || columns.ContainsKey("PREFERRED_NODE_AUTO"),
            "Rollback should drop both preferred-node columns from QRTZ_TRIGGERS.");
        AssertEx.True(columns.ContainsKey("TRIGGER_NAME"), "Rollback should keep the rest of QRTZ_TRIGGERS.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<IReadOnlyDictionary<string, ColumnInfo>> GetTriggerColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, \"notnull\", dflt_value FROM pragma_table_info('QRTZ_TRIGGERS');";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = new ColumnInfo
            {
                NotNull = reader.GetInt64(1) != 0,
                DefaultValue = await reader.IsDBNullAsync(2) ? null : reader.GetString(2)
            };
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed class ColumnInfo
    {
        public required bool NotNull { get; init; }

        public required string? DefaultValue { get; init; }
    }
}
