namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevelopmentMigrationTests : IDisposable
{
    private const string PreDevelopmentMigrationId = "20260718143054_AddAgentExecutionLogProvider";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Migration_AppliesDevelopmentSchemaAndSelectedFolderBindingThenRollsBack()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "development.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            AssertEx.True((await NamesAsync(connection, "table", "development_%").ConfigureAwait(false)).SetEquals([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks"
            ]));
            var indexes = await NamesAsync(connection, "index", "ux_development_%").ConfigureAwait(false);
            AssertEx.True(indexes.Contains("ux_development_attempts_one_active_per_task"));
            AssertEx.True(indexes.Contains("ux_development_events_project_sequence"));
            AssertEx.True(indexes.Contains("ux_development_events_operation_phase"));
            var attemptIndexes = await NamesAsync(connection, "index", "ix_development_attempts_%").ConfigureAwait(false);
            AssertEx.True(attemptIndexes.Contains("ix_development_attempts_task_started_at"));

            var selectedFolderColumn = AssertEx.NotNull(await ReadSelectedFolderColumnAsync(connection).ConfigureAwait(false));
            AssertEx.Equal("TEXT", selectedFolderColumn.Type);
            AssertEx.True(selectedFolderColumn.IsNullable);

            var developmentProjectIndexes = await ReadDevelopmentProjectIndexNamesAsync(connection).ConfigureAwait(false);
            AssertEx.True(developmentProjectIndexes.Contains("ix_development_projects_selected_folder_id"));

            var selectedFolderForeignKey = AssertEx.NotNull(await ReadSelectedFolderForeignKeyAsync(connection).ConfigureAwait(false));
            AssertEx.Equal("selected_folders", selectedFolderForeignKey.TargetTable);
            AssertEx.Equal("selected_folder_id", selectedFolderForeignKey.SourceColumn);
            AssertEx.Equal("id", selectedFolderForeignKey.TargetColumn);
            AssertEx.Equal("RESTRICT", selectedFolderForeignKey.OnDelete);
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = new SqliteConnection($"Data Source={databasePath}");
        await rolledBack.OpenAsync().ConfigureAwait(false);
        AssertEx.Empty(await NamesAsync(rolledBack, "table", "development_%").ConfigureAwait(false));
    }

    private static async Task<HashSet<string>> NamesAsync(SqliteConnection connection, string type, string pattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$pattern", pattern);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<ColumnSchema?> ReadSelectedFolderColumnAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, "notnull"
            FROM pragma_table_info('development_projects')
            WHERE name = 'selected_folder_id';
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        return new ColumnSchema(reader.GetString(0), IsNullable: reader.GetInt64(1) == 0);
    }

    private static async Task<HashSet<string>> ReadDevelopmentProjectIndexNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list('development_projects');";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task<ForeignKeySchema?> ReadSelectedFolderForeignKeyAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "table", "from", "to", on_delete
            FROM pragma_foreign_key_list('development_projects')
            WHERE "from" = 'selected_folder_id';
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        return new ForeignKeySchema(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private sealed record ColumnSchema(string Type, bool IsNullable);

    private sealed record ForeignKeySchema(string TargetTable, string SourceColumn, string TargetColumn, string OnDelete);
}
