namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddAgentDefinitionsMigrationTests : IDisposable
{
    private const string PreAgentDefinitionsMigrationId = "20260529173005_AddNodeSelectedFolders";
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
    public async Task MigrateAsync_WhenExistingConversationPresent_AddsAgentDefinitionsAndNullableBinding()
    {
        var databasePath = GetDatabasePath("agent-definitions-up.sqlite");
        var conversationId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAgentDefinitionsMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalConversationAsync(databasePath, conversationId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "agent_definitions").ConfigureAwait(false),
            "Migration should create the agent_definitions table.");

        // MigrateAsync applies every migration, so the column set reflects later additive migrations too: playbook
        // enabled is added by AddPlaybookActions, source and seed slug by AddAgentDefinitionSeedProvenance, and the
        // allowed skill ids json column by AddAgentSkills. This asserts the post-full-migrate shape.
        var definitionColumns = await GetAgentDefinitionColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(definitionColumns.SetEquals(new[]
        {
            "id",
            "name",
            "description",
            "instructions",
            "model_profile",
            "reasoning_effort",
            "kind",
            "allowed_tool_names_json",
            "allowed_skill_ids_json",
            "tool_approvals_json",
            "orchestration_topology_json",
            "version",
            "created_at_utc",
            "updated_at_utc",
            "playbook_enabled",
            "source",
            "seed_slug",
            // Added by the later AddAdaptiveAgentMemory migration; a full MigrateAsync() applies it, so they are part of
            // the expected set here.
            "default_temporary_chat",
            "memory_extraction_enabled",
            // Added by the later AddAgentDefinitionBaseScaffoldOptOut migration.
            "disable_base_scaffold",
            // Added by the later AddGenerationMetadata migration (AI-drafting provenance).
            "generation_metadata_json",
            // Added by the later AddToolSchemaTokenTelemetry migration (the per-agent tool-relevance opt-out).
            "disable_tool_relevance_filter"
        }), "agent_definitions should expose the mapped columns.");

        var conversationColumns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(conversationColumns.Contains("agent_definition_id"), "conversations.agent_definition_id should be added.");

        AssertEx.True(await ReadAgentDefinitionIdIsNullAsync(connection, conversationId).ConfigureAwait(false),
            "Existing conversations should default to a null agent_definition_id.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsAgentDefinitionsAndBinding()
    {
        var databasePath = GetDatabasePath("agent-definitions-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAgentDefinitionsMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "agent_definitions").ConfigureAwait(false),
            "Rollback should drop the agent_definitions table.");

        var conversationColumns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(conversationColumns.Contains("agent_definition_id"), "Rollback should drop conversations.agent_definition_id.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task InsertHistoricalConversationAsync(string databasePath, Guid conversationId)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                              VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, $purged);
                              """;
        command.Parameters.AddWithValue("$conversation_id", conversationId.ToString());
        command.Parameters.AddWithValue("$title", "Historical");
        command.Parameters.AddWithValue("$user_id", "node");
        command.Parameters.AddWithValue("$created_at_utc", value: 1234L);
        command.Parameters.AddWithValue("$last_seen_utc", value: 1234L);
        command.Parameters.AddWithValue("$purged", value: false);

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

    private static async Task<IReadOnlySet<string>> GetAgentDefinitionColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agent_definitions LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetConversationColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM conversations LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> ReadColumnNamesAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ReadAgentDefinitionIdIsNullAsync(SqliteConnection connection, Guid conversationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_definition_id FROM conversations WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
