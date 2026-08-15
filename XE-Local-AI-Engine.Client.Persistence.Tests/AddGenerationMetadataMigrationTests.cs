namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddGenerationMetadataMigrationTests : IDisposable
{
    private const string PreGenerationMetadataMigrationId = "20260814091525_AddBenchmarks";
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
    public async Task MigrateAsync_WhenApplied_AddsNullGenerationMetadataToPreExistingRows()
    {
        var databasePath = GetDatabasePath("generation-metadata-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreGenerationMetadataMigrationId).ConfigureAwait(false);
        }

        // A definition and a skill written before AI drafting existed, so the additive column is exercised on real rows
        // rather than on empty tables.
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertLegacyRowsAsync(connection).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var verifyConnection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            AssertEx.True((await GetColumnsAsync(verifyConnection, "agent_definitions").ConfigureAwait(false)).Contains("generation_metadata_json"),
                "Migration should add generation_metadata_json to agent_definitions.");
            AssertEx.True((await GetColumnsAsync(verifyConnection, "agent_skills").ConfigureAwait(false)).Contains("generation_metadata_json"),
                "Migration should add generation_metadata_json to agent_skills.");
        }

        // Null, not an empty blob: a row that predates AI drafting has no provenance, and an empty payload would read as
        // "drafted, details unknown". This context has no materialization interceptor, so the legacy placeholder blobs
        // in the other encrypted columns are read verbatim rather than failing authenticated decryption.
        await using var readContext = CreateContext(databasePath);
        AssertEx.Null((await readContext.AgentDefinitions.SingleAsync().ConfigureAwait(false)).GenerationMetadataJson,
            "A pre-existing definition should load with no generation metadata.");
        AssertEx.Null((await readContext.AgentSkills.SingleAsync().ConfigureAwait(false)).GenerationMetadataJson,
            "A pre-existing skill should load with no generation metadata.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsGenerationMetadataColumns()
    {
        var databasePath = GetDatabasePath("generation-metadata-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreGenerationMetadataMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False((await GetColumnsAsync(connection, "agent_definitions").ConfigureAwait(false)).Contains("generation_metadata_json"),
            "Rollback should drop generation_metadata_json from agent_definitions.");
        AssertEx.False((await GetColumnsAsync(connection, "agent_skills").ConfigureAwait(false)).Contains("generation_metadata_json"),
            "Rollback should drop generation_metadata_json from agent_skills.");
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

    private static async Task InsertLegacyRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO agent_definitions (id, name, instructions, allowed_tool_names_json, tool_approvals_json, version, created_at_utc, updated_at_utc)
                              VALUES ($definitionId, 'legacy-definition', x'00', '[]', '{}', 1, 1, 1);

                              INSERT INTO agent_skills (id, name, description, body, enabled, version, created_at_utc, updated_at_utc)
                              VALUES ($skillId, 'legacy-skill', x'00', x'00', 1, 1, 1, 1);
                              """;
        command.Parameters.AddWithValue("$definitionId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$skillId", Guid.NewGuid().ToString());
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(SqliteConnection connection, string tableName)
    {
        // pragma_table_info as a table-valued function takes the table name as a bound parameter, so the command text
        // stays a constant — a SELECT * with the name interpolated in would be string-built SQL (CA2100).
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(ordinal: 0));
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
