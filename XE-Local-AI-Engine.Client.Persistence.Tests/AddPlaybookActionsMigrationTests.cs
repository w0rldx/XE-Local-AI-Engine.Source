namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddPlaybookActionsMigrationTests : IDisposable
{
    private const string PrePlaybookActionsMigrationId = "20260530080425_AddMcpServers";
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
    public async Task MigrateAsync_WhenExistingAgentPresent_AddsPlaybookActionsAndPlaybookEnabled()
    {
        var databasePath = GetDatabasePath("playbook-actions-up.sqlite");
        var agentId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookActionsMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalAgentAsync(databasePath, agentId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "playbook_actions").ConfigureAwait(false),
            "Migration should create the playbook_actions table.");

        var actionColumns = await GetPlaybookActionColumnsAsync(connection).ConfigureAwait(false);
        // A full MigrateAsync() also applies the later AddPlaybookActionAnalysisColumns migration (analysis columns
        // source_feedback_ids, confidence), AddPlaybookEvalAndGoldenConversations (eval_result),
        // AddPlaybookActionEnabledAtUtc (enabled_at_utc) and AddAdaptiveAgentMemory (memory_scope) — hence they are
        // part of the expected set here.
        AssertEx.True(actionColumns.SetEquals(new[]
        {
            "id",
            "agent_definition_id",
            "state",
            "source",
            "trigger_condition",
            "behavior",
            "scope",
            "priority",
            "version",
            "created_at_utc",
            "updated_at_utc",
            "source_feedback_ids",
            "confidence",
            "eval_result",
            "enabled_at_utc",
            "memory_scope"
        }), "playbook_actions should expose the mapped columns.");

        var agentColumns = await GetAgentDefinitionColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(agentColumns.Contains("playbook_enabled"), "agent_definitions.playbook_enabled should be added.");

        AssertEx.True(await ReadPlaybookEnabledIsFalseAsync(connection, agentId).ConfigureAwait(false),
            "Existing agents should default to playbook_enabled = false.");
    }

    [Test]
    public async Task MigrateAsync_WhenAgentDeleted_CascadeDeletesPlaybookActions()
    {
        var databasePath = GetDatabasePath("playbook-actions-cascade.sqlite");
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        // SQLite enforces foreign keys only when the pragma is on for the connection.
        await EnableForeignKeysAsync(connection).ConfigureAwait(false);
        await InsertAgentAsync(connection, agentId).ConfigureAwait(false);
        await InsertPlaybookActionAsync(connection, actionId, agentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, await CountPlaybookActionsForAgentAsync(connection, agentId).ConfigureAwait(false));

        await DeleteAgentAsync(connection, agentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, await CountPlaybookActionsForAgentAsync(connection, agentId).ConfigureAwait(false));
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsPlaybookActionsAndPlaybookEnabled()
    {
        var databasePath = GetDatabasePath("playbook-actions-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookActionsMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "playbook_actions").ConfigureAwait(false),
            "Rollback should drop the playbook_actions table.");

        var agentColumns = await GetAgentDefinitionColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(agentColumns.Contains("playbook_enabled"), "Rollback should drop agent_definitions.playbook_enabled.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task InsertHistoricalAgentAsync(string databasePath, Guid agentId)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await InsertAgentAsync(connection, agentId).ConfigureAwait(false);
    }

    private static async Task InsertAgentAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO agent_definitions
                                  (id, name, instructions, kind, allowed_tool_names_json, tool_approvals_json, version, created_at_utc, updated_at_utc)
                              VALUES ($id, $name, $instructions, $kind, $allowed, $approvals, $version, $created, $updated);
                              """;
        command.Parameters.AddWithValue("$id", agentId.ToString());
        command.Parameters.AddWithValue("$name", "Historical");
        command.Parameters.AddWithValue("$instructions", new byte[]
        {
            1,
            2,
            3
        });
        command.Parameters.AddWithValue("$kind", value: 0);
        command.Parameters.AddWithValue("$allowed", "[]");
        command.Parameters.AddWithValue("$approvals", "{}");
        command.Parameters.AddWithValue("$version", value: 1);
        command.Parameters.AddWithValue("$created", value: 1234L);
        command.Parameters.AddWithValue("$updated", value: 1234L);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertPlaybookActionAsync(SqliteConnection connection, Guid actionId, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO playbook_actions
                                  (id, agent_definition_id, state, source, behavior, priority, version, created_at_utc, updated_at_utc)
                              VALUES ($id, $agent, $state, $source, $behavior, $priority, $version, $created, $updated);
                              """;
        command.Parameters.AddWithValue("$id", actionId.ToString());
        command.Parameters.AddWithValue("$agent", agentId.ToString());
        command.Parameters.AddWithValue("$state", value: 1);
        command.Parameters.AddWithValue("$source", value: 0);
        command.Parameters.AddWithValue("$behavior", new byte[]
        {
            9,
            8,
            7
        });
        command.Parameters.AddWithValue("$priority", value: 0);
        command.Parameters.AddWithValue("$version", value: 1);
        command.Parameters.AddWithValue("$created", value: 1234L);
        command.Parameters.AddWithValue("$updated", value: 1234L);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task DeleteAgentAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", agentId.ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> CountPlaybookActionsForAgentAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playbook_actions WHERE agent_definition_id = $id;";
        command.Parameters.AddWithValue("$id", agentId.ToString());
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task EnableForeignKeysAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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

    private static async Task<IReadOnlySet<string>> GetPlaybookActionColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM playbook_actions LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetAgentDefinitionColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agent_definitions LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> ReadColumnNamesAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ReadPlaybookEnabledIsFalseAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT playbook_enabled FROM agent_definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", agentId.ToString());
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is not null && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0L;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
