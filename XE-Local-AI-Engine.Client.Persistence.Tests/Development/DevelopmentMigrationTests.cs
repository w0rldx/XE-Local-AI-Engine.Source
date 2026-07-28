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
    private const string PreCommandProfileMigrationId = "20260726203016_AddKnowledgeVectorIdentity";
    private const string PreAttemptProfileMigrationId = "20260728184839_AddDevelopmentCommandProfile";

    private static readonly string[] DevelopmentTables =
    [
        "development_artifacts",
        "development_attempts",
        "development_events",
        "development_projects",
        "development_tasks"
    ];

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

    /// <summary>
    ///     Pins the command-profile migration alone: its <c>Down</c> must remove only the two new columns and leave the
    ///     Development tables — and the rows already in them — in place. This is deliberately narrower than
    ///     <see cref="Migration_AppliesDevelopmentSchemaAndSelectedFolderBindingThenRollsBack" />, which drives the whole
    ///     historical rollback chain down to the pre-Development schema and therefore expects the tables to be gone.
    /// </summary>
    [Test]
    public async Task CommandProfileMigration_RollsBackToPrecedingSchemaWithDevelopmentTablesAndRowsIntact()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "development-command-profile.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);

            var profileColumn = AssertEx.NotNull(await ReadColumnAsync(connection, "development_projects", "command_profile_json").ConfigureAwait(false));
            AssertEx.Equal("TEXT", profileColumn.Type);
            AssertEx.True(profileColumn.IsNullable);

            var digestColumn = AssertEx.NotNull(await ReadColumnAsync(connection, "development_artifacts", "command_profile_digest").ConfigureAwait(false));
            AssertEx.Equal("TEXT", digestColumn.Type);
            AssertEx.True(digestColumn.IsNullable);

            await SeedDevelopmentRowsAsync(connection, projectId, taskId, artifactId).ConfigureAwait(false);
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreCommandProfileMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = new SqliteConnection($"Data Source={databasePath}");
        await rolledBack.OpenAsync().ConfigureAwait(false);

        AssertEx.True((await NamesAsync(rolledBack, "table", "development_%").ConfigureAwait(false)).SetEquals(DevelopmentTables),
            "Rolling back the command-profile migration must not drop or recreate any Development table.");
        AssertEx.Null(await ReadColumnAsync(rolledBack, "development_projects", "command_profile_json").ConfigureAwait(false));
        AssertEx.Null(await ReadColumnAsync(rolledBack, "development_artifacts", "command_profile_digest").ConfigureAwait(false));

        AssertEx.Equal(1L, await ScalarAsync(rolledBack, ProjectCountSql, projectId).ConfigureAwait(false));
        AssertEx.Equal(1L, await ScalarAsync(rolledBack, TaskCountSql, taskId).ConfigureAwait(false));
        AssertEx.Equal(1L, await ScalarAsync(rolledBack, ArtifactCountSql, artifactId).ConfigureAwait(false));

        // command_profile_version is the artifact PROTOCOL version and a different column from the digest that was
        // just dropped; the rollback must leave it and its value untouched.
        AssertEx.Equal("development-workspace-v1", await ScalarAsync(rolledBack, ArtifactProfileVersionSql, artifactId).ConfigureAwait(false));
        AssertEx.Equal("origin/main", await ScalarAsync(rolledBack, ProjectBaseBranchSql, projectId).ConfigureAwait(false));
    }

    /// <summary>
    ///     Pins the attempt-profile migration alone (S1.5.3): its <c>Down</c> must remove only
    ///     <c>development_attempts.command_profile_json</c> and leave the Development tables, their rows, and the
    ///     project-level profile column from the preceding migration untouched. Same narrow assertion as
    ///     <see cref="CommandProfileMigration_RollsBackToPrecedingSchemaWithDevelopmentTablesAndRowsIntact" />, and
    ///     deliberately not the whole-chain rollback that
    ///     <see cref="Migration_AppliesDevelopmentSchemaAndSelectedFolderBindingThenRollsBack" /> drives.
    /// </summary>
    [Test]
    public async Task AttemptCommandProfileMigration_RollsBackToPrecedingSchemaWithDevelopmentTablesAndRowsIntact()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "development-attempt-command-profile.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);

            var attemptProfileColumn = AssertEx.NotNull(
                await ReadColumnAsync(connection, "development_attempts", "command_profile_json").ConfigureAwait(false));
            AssertEx.Equal("TEXT", attemptProfileColumn.Type);
            AssertEx.True(attemptProfileColumn.IsNullable);

            await SeedDevelopmentRowsAsync(connection, projectId, taskId, artifactId).ConfigureAwait(false);
            await SeedDevelopmentAttemptAsync(connection, attemptId, taskId).ConfigureAwait(false);
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreAttemptProfileMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = new SqliteConnection($"Data Source={databasePath}");
        await rolledBack.OpenAsync().ConfigureAwait(false);

        AssertEx.True((await NamesAsync(rolledBack, "table", "development_%").ConfigureAwait(false)).SetEquals(DevelopmentTables),
            "Rolling back the attempt-profile migration must not drop or recreate any Development table.");
        AssertEx.Null(await ReadColumnAsync(rolledBack, "development_attempts", "command_profile_json").ConfigureAwait(false));

        AssertEx.Equal(1L, await ScalarAsync(rolledBack, ProjectCountSql, projectId).ConfigureAwait(false));
        AssertEx.Equal(1L, await ScalarAsync(rolledBack, TaskCountSql, taskId).ConfigureAwait(false));
        AssertEx.Equal(1L, await ScalarAsync(rolledBack, ArtifactCountSql, artifactId).ConfigureAwait(false));
        AssertEx.Equal(1L, await ScalarAsync(rolledBack, AttemptCountSql, attemptId).ConfigureAwait(false));

        // The attempt row must survive with its other columns readable, and the filtered unique index that constrains
        // active attempts must still be there — a Down that rebuilt the table would silently lose it.
        AssertEx.Equal("Succeeded", await ScalarAsync(rolledBack, AttemptStatusSql, attemptId).ConfigureAwait(false));
        AssertEx.True((await NamesAsync(rolledBack, "index", "ux_development_attempts_%").ConfigureAwait(false))
            .Contains("ux_development_attempts_one_active_per_task"));

        // The PRECEDING migration's project-level column is a different column and must be left alone by this Down.
        AssertEx.NotNull(await ReadColumnAsync(rolledBack, "development_projects", "command_profile_json").ConfigureAwait(false));
        AssertEx.Equal("origin/main", await ScalarAsync(rolledBack, ProjectBaseBranchSql, projectId).ConfigureAwait(false));
    }

    private const string ProjectCountSql = "SELECT COUNT(*) FROM development_projects WHERE id = $id;";
    private const string TaskCountSql = "SELECT COUNT(*) FROM development_tasks WHERE id = $id;";
    private const string ArtifactCountSql = "SELECT COUNT(*) FROM development_artifacts WHERE id = $id;";
    private const string ArtifactProfileVersionSql = "SELECT command_profile_version FROM development_artifacts WHERE id = $id;";
    private const string ProjectBaseBranchSql = "SELECT base_branch FROM development_projects WHERE id = $id;";
    private const string AttemptCountSql = "SELECT COUNT(*) FROM development_attempts WHERE id = $id;";
    private const string AttemptStatusSql = "SELECT status FROM development_attempts WHERE id = $id;";

    private static async Task SeedDevelopmentAttemptAsync(SqliteConnection connection, Guid attemptId, Guid taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO development_attempts (id, task_id, predecessor_attempt_id, role, model_id, provider, status,
                                              started_at_utc, ended_at_utc, terminal_reason, input_tokens, output_tokens,
                                              start_operation_id, command_profile_json, version)
            VALUES ($attemptId, $taskId, NULL, 'Coder', 'model', 'local', 'Succeeded', 1, 2, NULL, NULL, NULL,
                    $operationId, '{"profileId":"generic-git"}', 1);
            """;
        command.Parameters.AddWithValue("$attemptId", attemptId.ToString());
        command.Parameters.AddWithValue("$taskId", taskId.ToString());
        command.Parameters.AddWithValue("$operationId", Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedDevelopmentRowsAsync(SqliteConnection connection, Guid projectId, Guid taskId, Guid artifactId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO development_projects (id, objective, selected_folder_id, repository_identity_hash, base_branch, status,
                                              egress_policy, coder_model_id, reviewer_model_id, max_tokens, max_duration_seconds,
                                              command_profile_json, configuration_version, trusted_repository_acknowledged,
                                              trusted_repository_policy_version, trusted_repository_acknowledged_at_utc,
                                              created_at_utc, updated_at_utc, version)
            VALUES ($projectId, X'6F', NULL, 'repo-hash', 'origin/main', 'Active', 'LocalOnly', NULL, NULL, NULL, NULL,
                    '{"build":{"executable":"dotnet"}}', 1, 0, NULL, NULL, 1, 1, 1);

            INSERT INTO development_tasks (id, project_id, title, requirements, acceptance_criteria_json, status,
                                           current_review_round, max_review_rounds, blocked_reason, blocked_at_utc,
                                           approved_subject_hash, created_at_utc, updated_at_utc, version)
            VALUES ($taskId, $projectId, X'74', X'72', X'5B5D', 'Planned', 0, 3, NULL, NULL, NULL, 1, 1, 1);

            INSERT INTO development_artifacts (id, project_id, task_id, attempt_id, kind, schema_version, content_json,
                                               managed_reference, content_hash, byte_count, created_at_utc, base_commit,
                                               subject_hash, changed_files_manifest_hash, input_artifact_ids_json,
                                               command_profile_version, command_profile_digest, is_valid)
            VALUES ($artifactId, $projectId, $taskId, NULL, 'WorkspaceSnapshot', 1, NULL, NULL, 'content-hash', 1, 1, NULL,
                    NULL, NULL, NULL, 'development-workspace-v1',
                    '0000000000000000000000000000000000000000000000000000000000000000', 1);
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$taskId", taskId.ToString());
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, Guid id)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Every caller passes a fixed test literal; the only variable, the id, is a parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$id", id.ToString());
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is DBNull ? null : value;
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

    private static Task<ColumnSchema?> ReadSelectedFolderColumnAsync(SqliteConnection connection) =>
        ReadColumnAsync(connection, "development_projects", "selected_folder_id");

    private static async Task<ColumnSchema?> ReadColumnAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, "notnull"
            FROM pragma_table_info($table)
            WHERE name = $column;
            """;
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
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
