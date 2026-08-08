namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddAgentSkillsMigrationTests : IDisposable
{
    private const string PreAgentSkillsMigrationId = "20260602195614_AddAgentDefinitionSeedProvenance";
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
    public async Task MigrateAsync_WhenApplied_CreatesAgentSkillsAndAddsAllowedSkillIdsColumn()
    {
        var databasePath = GetDatabasePath("agent-skills-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAgentSkillsMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "agent_skills").ConfigureAwait(false),
            "Migration should create the agent_skills table.");

        var columns = await GetAgentSkillsColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "name",
            "description",
            "body",
            "enabled",
            "version",
            "created_at_utc",
            "updated_at_utc",

            // Added later by AddAgentSkillImportProvenance; asserted here too because this test pins the whole column
            // set after every migration has run, not just the ones this file names.
            "frontmatter_json",
            "origin",
            "source_uri",
            "imported_at_utc",
            "content_sha256"
        }), "agent_skills should expose the mapped columns.");

        AssertEx.True(await NameUsesNoCaseUniqueAsync(connection).ConfigureAwait(false),
            "agent_skills.name should be NOCASE and uniquely indexed.");

        var agentColumns = await GetAgentDefinitionsColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(agentColumns.Contains("allowed_skill_ids_json"),
            "Migration should add allowed_skill_ids_json to agent_definitions.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsAgentSkillsAndColumn()
    {
        var databasePath = GetDatabasePath("agent-skills-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAgentSkillsMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "agent_skills").ConfigureAwait(false),
            "Rollback should drop the agent_skills table.");

        var agentColumns = await GetAgentDefinitionsColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(agentColumns.Contains("allowed_skill_ids_json"),
            "Rollback should drop allowed_skill_ids_json from agent_definitions.");
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

    private static async Task<IReadOnlySet<string>> GetAgentSkillsColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agent_skills LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetAgentDefinitionsColumnsAsync(SqliteConnection connection)
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

    private static async Task<bool> NameUsesNoCaseUniqueAsync(SqliteConnection connection)
    {
        // The CREATE TABLE statement records the per-column COLLATE clause; confirm name carries COLLATE NOCASE, and the
        // unique index over name exists. PRAGMA/literal SQL only — free of caller-supplied input.
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_skills';";
        var tableSql = await tableCommand.ExecuteScalarAsync().ConfigureAwait(false) as string;

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'agent_skills' AND sql LIKE '%UNIQUE%';";
        var indexSql = await indexCommand.ExecuteScalarAsync().ConfigureAwait(false) as string;

        return tableSql is not null
               && tableSql.Contains("name", StringComparison.Ordinal)
               && tableSql.Contains("NOCASE", StringComparison.OrdinalIgnoreCase)
               && indexSql is not null
               && indexSql.Contains("name", StringComparison.Ordinal);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
