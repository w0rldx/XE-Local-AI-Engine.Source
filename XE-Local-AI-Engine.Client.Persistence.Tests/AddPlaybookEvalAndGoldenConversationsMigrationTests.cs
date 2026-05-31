namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddPlaybookEvalAndGoldenConversationsMigrationTests : IDisposable
{
    private const string PrePlaybookEvalMigrationId = "20260531082914_AddPlaybookActionAnalysisColumns";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_AddsEvalResultColumnAndGoldenConversationsTable()
    {
        var databasePath = GetDatabasePath("eval-golden-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookEvalMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var actionColumns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);
        AssertEx.True(actionColumns.ContainsKey("eval_result"), "Migration should add the eval_result column.");
        AssertEx.False(actionColumns["eval_result"], "eval_result should be nullable.");

        AssertEx.True(await TableExistsAsync(connection, "golden_conversations").ConfigureAwait(false),
            "Migration should create the golden_conversations table.");

        var goldenColumns = await GetGoldenConversationColumnInfoAsync(connection).ConfigureAwait(false);
        AssertEx.True(new HashSet<string>(goldenColumns.Keys, StringComparer.Ordinal).SetEquals(new[]
        {
            "id", "agent_definition_id", "title", "input_turns", "assertion", "rubric", "enabled",
            "created_at_utc", "updated_at_utc"
        }), "golden_conversations should expose the mapped columns.");
        AssertEx.True(goldenColumns["input_turns"], "input_turns should be non-nullable.");
        AssertEx.False(goldenColumns["assertion"], "assertion should be nullable.");
        AssertEx.False(goldenColumns["rubric"], "rubric should be nullable.");

        AssertEx.True(await IndexExistsAsync(connection, "IX_golden_conversations_agent_definition_id").ConfigureAwait(false),
            "Migration should index golden_conversations.agent_definition_id.");
    }

    [Test]
    public async Task MigrateAsync_WhenAgentDeleted_CascadeDeletesGoldenConversations()
    {
        var databasePath = GetDatabasePath("golden-cascade.sqlite");
        var agentId = Guid.NewGuid();
        var goldenId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        // SQLite enforces foreign keys only when the pragma is on for the connection.
        await EnableForeignKeysAsync(connection).ConfigureAwait(false);
        await InsertAgentAsync(connection, agentId).ConfigureAwait(false);
        await InsertGoldenConversationAsync(connection, goldenId, agentId).ConfigureAwait(false);

        AssertEx.Equal(1L, await CountGoldenConversationsForAgentAsync(connection, agentId).ConfigureAwait(false));

        await DeleteAgentAsync(connection, agentId).ConfigureAwait(false);

        AssertEx.Equal(0L, await CountGoldenConversationsForAgentAsync(connection, agentId).ConfigureAwait(false));
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsEvalResultColumnAndGoldenConversationsTable()
    {
        var databasePath = GetDatabasePath("eval-golden-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookEvalMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var actionColumns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);
        AssertEx.False(actionColumns.ContainsKey("eval_result"), "Rollback should drop the eval_result column.");
        // The pre-eval playbook_actions schema must survive the rollback intact.
        AssertEx.True(actionColumns.ContainsKey("behavior"), "Rollback should retain the original playbook_actions schema.");

        AssertEx.False(await TableExistsAsync(connection, "golden_conversations").ConfigureAwait(false),
            "Rollback should drop the golden_conversations table.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
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
        command.Parameters.AddWithValue("$instructions", new byte[] { 1, 2, 3 });
        command.Parameters.AddWithValue("$kind", 0);
        command.Parameters.AddWithValue("$allowed", "[]");
        command.Parameters.AddWithValue("$approvals", "{}");
        command.Parameters.AddWithValue("$version", 1);
        command.Parameters.AddWithValue("$created", 1234L);
        command.Parameters.AddWithValue("$updated", 1234L);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertGoldenConversationAsync(SqliteConnection connection, Guid goldenId, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO golden_conversations
                                  (id, agent_definition_id, title, input_turns, enabled, created_at_utc, updated_at_utc)
                              VALUES ($id, $agent, $title, $input, $enabled, $created, $updated);
                              """;
        command.Parameters.AddWithValue("$id", goldenId.ToString());
        command.Parameters.AddWithValue("$agent", agentId.ToString());
        command.Parameters.AddWithValue("$title", "Case A");
        command.Parameters.AddWithValue("$input", new byte[] { 9, 8, 7 });
        command.Parameters.AddWithValue("$enabled", 1);
        command.Parameters.AddWithValue("$created", 1234L);
        command.Parameters.AddWithValue("$updated", 1234L);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task DeleteAgentAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", agentId.ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> CountGoldenConversationsForAgentAsync(SqliteConnection connection, Guid agentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM golden_conversations WHERE agent_definition_id = $id;";
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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<IReadOnlyDictionary<string, bool>> GetPlaybookActionColumnInfoAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        // Literal PRAGMA (no interpolation) so the analyzer cannot flag a dynamic command string (CA2100).
        command.CommandText = "PRAGMA table_info(playbook_actions);";
        return await ReadColumnInfoAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, bool>> GetGoldenConversationColumnInfoAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(golden_conversations);";
        return await ReadColumnInfoAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ReadColumnInfoAsync(SqliteCommand command)
    {
        // PRAGMA table_info exposes the per-column NOT NULL flag, which lets the test assert column nullability.
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
