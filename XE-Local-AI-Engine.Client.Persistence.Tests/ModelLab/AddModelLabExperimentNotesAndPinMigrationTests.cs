namespace XE_Local_AI_Engine.Client.Persistence.Tests.ModelLab;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddModelLabExperimentNotesAndPin</c> adds three columns that an existing row reads as unpinned with no note, and drops them on a one-step rollback.</summary>
[Category(TestCategories.Integration)]
public sealed class AddModelLabExperimentNotesAndPinMigrationTests : IDisposable
{
    private const string PreviousMigrationId = "20261114011749_AddListOrderIndexes";

    private const string MigrationId = "20261115160852_AddModelLabExperimentNotesAndPin";

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
    public async Task Upgrade_AddsTheColumns_AndAnExistingRowIsUnpinnedWithNoNoteOrVersion()
    {
        var databasePath = GetDatabasePath("up.sqlite");
        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreviousMigrationId);
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            AssertEx.False((await GetColumnNamesAsync(connection)).Contains("is_pinned"), "The column must not exist before this migration.");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO model_lab_experiments (id, status, kind, repo_id, revision, base_artifact_id, installed_model_name, prompt,
                                     license_confirmation_json, created_at_utc, updated_at_utc)
                                 VALUES ('00000000-0000-0000-0000-000000000001', 'Succeeded', 'Trace', 'org/repo', 'rev', '00000000-0000-0000-0000-000000000002',
                                     'org/model:Q4', X'01', X'01', 1, 1);
                                 """;
            AssertEx.Equal(expected: 1, await insert.ExecuteNonQueryAsync());
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(MigrationId);
        }

        await using var upgraded = await OpenConnectionAsync(databasePath);
        await using var read = upgraded.CreateCommand();
        read.CommandText = "SELECT is_pinned, notes IS NULL, app_version IS NULL FROM model_lab_experiments;";
        await using var reader = await read.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync(), "The row from before the migration is still there.");
        AssertEx.Equal(expected: 0L, reader.GetInt64(0));
        AssertEx.Equal(expected: 1L, reader.GetInt64(1));
        AssertEx.Equal(expected: 1L, reader.GetInt64(2));
    }

    [Test]
    public async Task RollbackOneStep_DropsTheColumns_AndKeepsTheOnesBeforeThem()
    {
        var databasePath = GetDatabasePath("down.sqlite");
        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, MigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        var columns = await GetColumnNamesAsync(connection);
        AssertEx.False(columns.Contains("notes") || columns.Contains("is_pinned") || columns.Contains("app_version"), "A rollback drops all three columns.");
        AssertEx.True(columns.Contains("kind") && columns.Contains("trace_byte_count") && columns.Contains("launch_receipt_json"), "A rollback keeps the earlier columns.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        await using var context = CreateContext(GetDatabasePath("drift.sqlite"));

        AssertEx.False(context.Database.HasPendingModelChanges(), "The NodeChat model has drifted from the latest migration snapshot; regenerate the migration.");
    }

    private NodeChatDbContext CreateContext(string databasePath) =>
        AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(model_lab_experiments);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
