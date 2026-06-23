namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Round-trips the additive adaptive-agent-memory migration (M1 memory_scope, M2 agent_execution_logs, M3
///     conversations.memory_excluded, M4 agent_definitions.default_temporary_chat, M5
///     agent_definitions.memory_extraction_enabled). Asserts the schema after applying up from the prior migration and
///     after rolling back.
/// </summary>
public sealed class AddAdaptiveAgentMemoryMigrationTests : IDisposable
{
    private const string PreAdaptiveMemoryMigrationId = "20260617222625_AddModelProviderMap";
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
    public async Task MigrateAsync_AddsMemoryScopeColumn()
    {
        var columns = await MigrateUpThenReadColumnsAsync("memory-scope-up.sqlite", "playbook_actions").ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("memory_scope"), "Migration should add the memory_scope column.");
        AssertEx.False(columns["memory_scope"], "memory_scope should be nullable.");
    }

    [Test]
    public async Task MigrateAsync_AddsDefaultTemporaryChatColumn()
    {
        var columns = await MigrateUpThenReadColumnsAsync("default-temp-chat-up.sqlite", "agent_definitions").ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("default_temporary_chat"), "Migration should add the default_temporary_chat column.");
        AssertEx.True(columns["default_temporary_chat"], "default_temporary_chat should be NOT NULL (DEFAULT 0).");
    }

    [Test]
    public async Task MigrateAsync_AddsMemoryExtractionEnabledColumn()
    {
        var columns = await MigrateUpThenReadColumnsAsync("memory-extraction-enabled-up.sqlite", "agent_definitions").ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("memory_extraction_enabled"), "Migration should add the memory_extraction_enabled column.");
        AssertEx.True(columns["memory_extraction_enabled"], "memory_extraction_enabled should be NOT NULL (DEFAULT 1).");
    }

    [Test]
    public async Task MigrateAsync_AddsMemoryExcludedColumnToRawConversationsTable()
    {
        var columns = await MigrateUpThenReadColumnsAsync("memory-excluded-up.sqlite", "conversations").ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("memory_excluded"), "Migration should add the memory_excluded column to the raw conversations table.");
        AssertEx.True(columns["memory_excluded"], "memory_excluded should be NOT NULL (DEFAULT 0).");
    }

    [Test]
    public async Task MigrateAsync_CreatesAgentExecutionLogsTable_MetadataOnly()
    {
        var columns = await MigrateUpThenReadColumnsAsync("exec-log-up.sqlite", "agent_execution_logs").ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("id"), "agent_execution_logs should have an id column.");
        AssertEx.True(columns.ContainsKey("agent_definition_id"), "agent_execution_logs should have an agent_definition_id column.");
        AssertEx.True(columns.ContainsKey("conversation_id"), "agent_execution_logs should have a conversation_id column.");
        AssertEx.True(columns.ContainsKey("message_id"), "agent_execution_logs should have a message_id column.");
        AssertEx.True(columns.ContainsKey("model_name"), "agent_execution_logs should have a model_name column.");
        AssertEx.True(columns.ContainsKey("config_hash"), "agent_execution_logs should have a config_hash column.");
        AssertEx.True(columns.ContainsKey("latency_ms"), "agent_execution_logs should have a latency_ms column.");
        AssertEx.True(columns.ContainsKey("prompt_tokens"), "agent_execution_logs should have a prompt_tokens column.");
        AssertEx.True(columns.ContainsKey("completion_tokens"), "agent_execution_logs should have a completion_tokens column.");
        AssertEx.True(columns.ContainsKey("success"), "agent_execution_logs should have a success column.");
        AssertEx.True(columns.ContainsKey("error_class"), "agent_execution_logs should have an error_class column.");
        AssertEx.True(columns.ContainsKey("created_at_utc"), "agent_execution_logs should have a created_at_utc column.");

        // No message content columns may exist on the metadata-only log table.
        AssertEx.False(columns.ContainsKey("content"), "agent_execution_logs must NOT carry message content.");
        AssertEx.False(columns.ContainsKey("behavior"), "agent_execution_logs must NOT carry behavior text.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsAdaptiveMemorySchema()
    {
        var databasePath = GetDatabasePath("adaptive-memory-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAdaptiveMemoryMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var playbookColumns = await GetColumnInfoAsync(connection, "playbook_actions").ConfigureAwait(false);
        AssertEx.False(playbookColumns.ContainsKey("memory_scope"), "Rollback should drop the memory_scope column.");
        AssertEx.True(playbookColumns.ContainsKey("behavior"), "Rollback should retain the original playbook_actions schema.");

        var agentColumns = await GetColumnInfoAsync(connection, "agent_definitions").ConfigureAwait(false);
        AssertEx.False(agentColumns.ContainsKey("default_temporary_chat"), "Rollback should drop the default_temporary_chat column.");
        AssertEx.False(agentColumns.ContainsKey("memory_extraction_enabled"), "Rollback should drop the memory_extraction_enabled column.");
        AssertEx.True(agentColumns.ContainsKey("playbook_enabled"), "Rollback should retain the original agent_definitions schema.");

        var conversationColumns = await GetColumnInfoAsync(connection, "conversations").ConfigureAwait(false);
        AssertEx.False(conversationColumns.ContainsKey("memory_excluded"), "Rollback should drop the memory_excluded column.");
        AssertEx.True(conversationColumns.ContainsKey("origin"), "Rollback should retain the original conversations schema.");

        var execLogColumns = await GetColumnInfoAsync(connection, "agent_execution_logs").ConfigureAwait(false);
        AssertEx.Equal(expected: 0, execLogColumns.Count, "Rollback should drop the agent_execution_logs table entirely.");
    }

    private async Task<IReadOnlyDictionary<string, bool>> MigrateUpThenReadColumnsAsync(string fileName, string tableName)
    {
        var databasePath = GetDatabasePath(fileName);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAdaptiveMemoryMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        return await GetColumnInfoAsync(connection, tableName).ConfigureAwait(false);
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

    private static async Task<IReadOnlyDictionary<string, bool>> GetColumnInfoAsync(SqliteConnection connection, string tableName)
    {
        // PRAGMA table_info returns no rows for a non-existent table, so the rollback test can assert a dropped table by
        // an empty result. The bool value is the per-column NOT NULL flag.
        await using var command = connection.CreateCommand();
        // PRAGMA does not accept bound parameters; tableName is a compile-time test constant (never user input), and
        // SQLite's PRAGMA grammar rejects a parameter placeholder here.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
        command.CommandText = $"PRAGMA table_info({tableName});";
#pragma warning restore CA2100

        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            var notNull = reader.GetInt64(reader.GetOrdinal("notnull")) != 0L;
            columns[name] = notNull;
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
