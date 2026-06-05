namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Harvest follow-up migration <c>AddGoldenConversationHarvestProvenance</c>: adds the <c>source</c> /
///     <c>source_message_id</c> / <c>source_conversation_id</c> columns to <c>golden_conversations</c>. Existing rows
///     default <c>source = 0</c> (Manual) with null provenance ids; rollback drops the three columns. Mirrors
///     <see cref="NodeChatOriginMigrationTests" /> (historical-row insert + up/down assertions over raw columns).
/// </summary>
public sealed class AddGoldenConversationHarvestProvenanceMigrationTests : IDisposable
{
    private const string PreHarvestProvenanceMigrationId = "20260531133736_AddPlaybookActionEnabledAtUtc";
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
    public async Task MigrateAsync_WhenExistingRowPresent_AddsProvenanceColumnsDefaultingToManual()
    {
        var databasePath = GetDatabasePath("provenance-defaults.sqlite");
        var goldenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreHarvestProvenanceMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalGoldenRowAsync(databasePath, goldenId, agentId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var goldenColumns = await GetGoldenConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(goldenColumns.Contains("source"), "Migration should add golden_conversations.source.");
        AssertEx.True(goldenColumns.Contains("source_message_id"), "Migration should add golden_conversations.source_message_id.");
        AssertEx.True(goldenColumns.Contains("source_conversation_id"), "Migration should add golden_conversations.source_conversation_id.");

        AssertEx.Equal(0L, await ReadSourceAsync(connection, goldenId).ConfigureAwait(false), "Existing golden rows should default to source 0 (Manual).");
        AssertEx.True(await IsSourceMessageIdNullAsync(connection, goldenId).ConfigureAwait(false), "Existing rows should have a null source_message_id.");
        AssertEx.True(await IsSourceConversationIdNullAsync(connection, goldenId).ConfigureAwait(false), "Existing rows should have a null source_conversation_id.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsProvenanceColumns()
    {
        var databasePath = GetDatabasePath("provenance-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreHarvestProvenanceMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var goldenColumns = await GetGoldenConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(goldenColumns.Contains("source"), "Rollback should drop golden_conversations.source.");
        AssertEx.False(goldenColumns.Contains("source_message_id"), "Rollback should drop golden_conversations.source_message_id.");
        AssertEx.False(goldenColumns.Contains("source_conversation_id"), "Rollback should drop golden_conversations.source_conversation_id.");
        // The pre-provenance golden schema must survive the rollback intact.
        AssertEx.True(goldenColumns.Contains("input_turns"), "Rollback should retain the original golden_conversations schema.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task InsertHistoricalGoldenRowAsync(string databasePath, Guid goldenId, Guid agentId)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        // Insert the agent first — golden_conversations.agent_definition_id is a real FK with cascade delete.
        await using (var command = connection.CreateCommand())
        {
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
            command.Parameters.AddWithValue("$kind", 0);
            command.Parameters.AddWithValue("$allowed", "[]");
            command.Parameters.AddWithValue("$approvals", "{}");
            command.Parameters.AddWithValue("$version", 1);
            command.Parameters.AddWithValue("$created", 1234L);
            command.Parameters.AddWithValue("$updated", 1234L);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Insert only the pre-existing golden columns (no provenance) so the migration must back-fill the defaults.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO golden_conversations
                                      (id, agent_definition_id, title, input_turns, enabled, created_at_utc, updated_at_utc)
                                  VALUES ($id, $agent, $title, $input, $enabled, $created, $updated);
                                  """;
            command.Parameters.AddWithValue("$id", goldenId.ToString());
            command.Parameters.AddWithValue("$agent", agentId.ToString());
            command.Parameters.AddWithValue("$title", "Historical case");
            command.Parameters.AddWithValue("$input", new byte[]
            {
                9,
                8,
                7
            });
            command.Parameters.AddWithValue("$enabled", 1);
            command.Parameters.AddWithValue("$created", 1234L);
            command.Parameters.AddWithValue("$updated", 1234L);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlySet<string>> GetGoldenConversationColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM golden_conversations LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<long> ReadSourceAsync(SqliteConnection connection, Guid goldenId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source FROM golden_conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", goldenId.ToString());
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is long source ? source : throw new AssertionException("Expected a non-null source value.");
    }

    private static async Task<bool> IsSourceMessageIdNullAsync(SqliteConnection connection, Guid goldenId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_message_id FROM golden_conversations WHERE id = $id;";
        return await IsScalarNullAsync(command, goldenId).ConfigureAwait(false);
    }

    private static async Task<bool> IsSourceConversationIdNullAsync(SqliteConnection connection, Guid goldenId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_conversation_id FROM golden_conversations WHERE id = $id;";
        return await IsScalarNullAsync(command, goldenId).ConfigureAwait(false);
    }

    private static async Task<bool> IsScalarNullAsync(SqliteCommand command, Guid goldenId)
    {
        command.Parameters.AddWithValue("$id", goldenId.ToString());
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
