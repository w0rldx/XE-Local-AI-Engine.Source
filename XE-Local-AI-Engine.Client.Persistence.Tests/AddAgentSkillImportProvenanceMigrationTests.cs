namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddAgentSkillImportProvenanceMigrationTests : IDisposable
{
    private const string PreProvenanceMigrationId = "20260803163513_HashMcpServerApiKey";
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
    public async Task MigrateAsync_WhenApplied_AddsProvenanceColumnsAndResourceTable()
    {
        var databasePath = GetDatabasePath("skill-provenance-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreProvenanceMigrationId).ConfigureAwait(false);
        }

        // A skill written before the import feature existed, so the backfill below is exercised on a real row rather
        // than on an empty table.
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertLegacySkillAsync(connection).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var verifyConnection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var skillColumns = await GetColumnsAsync(verifyConnection, "agent_skills").ConfigureAwait(false);
        AssertEx.True(skillColumns.IsSupersetOf(new[]
        {
            "frontmatter_json",
            "origin",
            "source_uri",
            "imported_at_utc",
            "content_sha256"
        }), "Migration should add the provenance columns to agent_skills.");

        // origin backfills to 0 (Local): a row that predates the import path is operator-authored by definition, and
        // reading it as Imported would fence content that never needed fencing.
        AssertEx.Equal(expected: 0L, await ReadLegacyOriginAsync(verifyConnection).ConfigureAwait(false));

        AssertEx.True(await TableExistsAsync(verifyConnection, "agent_skill_resources").ConfigureAwait(false),
            "Migration should create the agent_skill_resources table.");

        var resourceColumns = await GetColumnsAsync(verifyConnection, "agent_skill_resources").ConfigureAwait(false);
        AssertEx.True(resourceColumns.SetEquals(new[]
        {
            "id",
            "skill_id",
            "name",
            "description",
            "media_type",
            "content",
            "size_bytes"
        }), "agent_skill_resources should expose the mapped columns.");

        AssertEx.True(await ResourceNameIsNoCaseUniquePerSkillAsync(verifyConnection).ConfigureAwait(false),
            "agent_skill_resources.name should be NOCASE and uniquely indexed per skill.");

        AssertEx.True(await ForeignKeyCascadesAsync(verifyConnection).ConfigureAwait(false),
            "agent_skill_resources.skill_id should cascade on delete.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsProvenanceColumnsAndResourceTable()
    {
        var databasePath = GetDatabasePath("skill-provenance-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreProvenanceMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "agent_skill_resources").ConfigureAwait(false),
            "Rollback should drop the agent_skill_resources table.");

        var skillColumns = await GetColumnsAsync(connection, "agent_skills").ConfigureAwait(false);
        AssertEx.True(skillColumns.Overlaps(new[]
        {
            "id",
            "name"
        }), "Rollback should leave agent_skills itself in place.");
        AssertEx.False(skillColumns.Overlaps(new[]
        {
            "frontmatter_json",
            "origin",
            "source_uri",
            "imported_at_utc",
            "content_sha256"
        }), "Rollback should drop the provenance columns from agent_skills.");
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

    private static async Task InsertLegacySkillAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO agent_skills (id, name, description, body, enabled, version, created_at_utc, updated_at_utc)
                              VALUES ($id, 'legacy-skill', x'00', x'00', 1, 1, 1, 1);
                              """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> ReadLegacyOriginAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT origin FROM agent_skills WHERE name = 'legacy-skill';";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
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

    private static async Task<bool> ResourceNameIsNoCaseUniquePerSkillAsync(SqliteConnection connection)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_skill_resources';";
        var tableSql = await tableCommand.ExecuteScalarAsync().ConfigureAwait(false) as string;

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'agent_skill_resources' AND sql LIKE '%UNIQUE%';";
        var indexSql = await indexCommand.ExecuteScalarAsync().ConfigureAwait(false) as string;

        return tableSql is not null
               && tableSql.Contains("NOCASE", StringComparison.OrdinalIgnoreCase)
               && indexSql is not null
               && indexSql.Contains("skill_id", StringComparison.Ordinal)
               && indexSql.Contains("name", StringComparison.Ordinal);
    }

    private static async Task<bool> ForeignKeyCascadesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_list('agent_skill_resources');";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (string.Equals(reader["table"] as string, "agent_skills", StringComparison.Ordinal)
                && string.Equals(reader["on_delete"] as string, "CASCADE", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
