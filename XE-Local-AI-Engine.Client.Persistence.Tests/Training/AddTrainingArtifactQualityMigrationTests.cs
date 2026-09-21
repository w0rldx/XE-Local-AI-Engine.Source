namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AddTrainingArtifactQualityMigrationTests : IDisposable
{
    private const string DatasetFingerprint = "v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string QualityMigrationId = "20260817124108_AddTrainingArtifactQuality";
    private const string PreQualityMigrationId = "20260816211213_RemoveBenchmarkRunJudgeColumns";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Migration_DefaultsLegacyEvaluationsToInstalledModel_AndMatchesSnapshot()
    {
        _ = Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "quality.sqlite");
        await MigratedDatabaseTemplate.CopyChatAtAsync(path, PreQualityMigrationId);

        await using (var legacyConnection = new SqliteConnection($"Data Source={path}"))
        {
            await legacyConnection.OpenAsync();
            var (datasetId, _) = await SeedParentChainAsync(legacyConnection);
            await using var insert = legacyConnection.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO training_evaluation_runs
                                     (id, model_name, dataset_id, dataset_content_fingerprint, membership_json, status,
                                      total_count, scored_count, passed_count, version, created_at_utc, updated_at_utc)
                                 VALUES
                                     ($id, 'legacy', $dataset, $fingerprint, X'00',
                                      'Succeeded', 1, 1, 1, 1, 0, 0);
                                 """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$dataset", datasetId.ToString());
            insert.Parameters.AddWithValue("$fingerprint", DatasetFingerprint);
            AssertEx.Equal(expected: 1, await insert.ExecuteNonQueryAsync());
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.MigrateAsync();
            AssertEx.False(context.Database.HasPendingModelChanges());
        }

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT target_kind FROM training_evaluation_runs WHERE model_name = 'legacy';";
        AssertEx.Equal("InstalledModel", (string)(await query.ExecuteScalarAsync())!);
    }

    [Test]
    public async Task Migration_FromQualitySchema_PreservesDecisionAndAddsNullableDiscardAudit()
    {
        _ = Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "discard-upgrade.sqlite");
        var artifactId = Guid.NewGuid();
        var decision = "decision"u8.ToArray();
        const string kind = "MergedGguf";
        const string stagedPath = "staged.gguf";
        const string smokeState = "Passed";
        await MigratedDatabaseTemplate.CopyChatAtAsync(path, QualityMigrationId);

        await using (var seedConnection = new SqliteConnection($"Data Source={path}"))
        {
            await seedConnection.OpenAsync();
            var (_, runId) = await SeedParentChainAsync(seedConnection);
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = """
                               INSERT INTO training_artifacts
                                   (id, run_id, kind, path, sha256, size_bytes, smoke_state, smoke_reason, committed_model_name,
                                    quality_comparison_id, quality_decision_json, version, created_at_utc, updated_at_utc)
                               VALUES
                                   ($id, $run, $kind, $path, $sha, 4, $smoke, NULL, NULL, $comparison, $decision, 3, 0, 0);
                               """;
            seed.Parameters.AddWithValue("$id", artifactId.ToString());
            seed.Parameters.AddWithValue("$run", runId.ToString());
            seed.Parameters.AddWithValue("$kind", kind);
            seed.Parameters.AddWithValue("$path", stagedPath);
            seed.Parameters.AddWithValue("$sha", new string('a', 64));
            seed.Parameters.AddWithValue("$smoke", smokeState);
            seed.Parameters.AddWithValue("$comparison", Guid.NewGuid().ToString());
            seed.Parameters.AddWithValue("$decision", decision);
            AssertEx.Equal(expected: 1, await seed.ExecuteNonQueryAsync());
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.MigrateAsync();
            AssertEx.False(context.Database.HasPendingModelChanges());
        }

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT quality_decision_json, discarded_at_utc, discard_reason, discard_cleanup_pending FROM training_artifacts WHERE id = $id;";
        query.Parameters.AddWithValue("$id", artifactId.ToString());
        await using var reader = await query.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync());
        AssertEx.True(((byte[])reader.GetValue(0)).AsSpan().SequenceEqual(decision));
        AssertEx.True(await reader.IsDBNullAsync(1));
        AssertEx.True(await reader.IsDBNullAsync(2));
        AssertEx.Equal(expected: 0L, reader.GetInt64(3));
    }

    [Test]
    public async Task Migration_FromPreQualitySchema_GrandfathersPromotedArtifactWithoutDecision()
    {
        _ = Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "promoted-upgrade.sqlite");
        var artifactId = Guid.NewGuid();
        await MigratedDatabaseTemplate.CopyChatAtAsync(path, PreQualityMigrationId);

        await using (var seedConnection = new SqliteConnection($"Data Source={path}"))
        {
            await seedConnection.OpenAsync();
            var (_, runId) = await SeedParentChainAsync(seedConnection);
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = """
                               INSERT INTO training_artifacts
                                   (id, run_id, kind, path, sha256, size_bytes, smoke_state, smoke_reason, committed_model_name,
                                    version, created_at_utc, updated_at_utc)
                               VALUES
                                   ($id, $run, 'MergedGguf', 'legacy.gguf', $sha, 4, 'Passed', NULL, 'legacy:Q4_K_M', 3, 0, 0);
                               """;
            seed.Parameters.AddWithValue("$id", artifactId.ToString());
            seed.Parameters.AddWithValue("$run", runId.ToString());
            seed.Parameters.AddWithValue("$sha", new string('b', 64));
            AssertEx.Equal(expected: 1, await seed.ExecuteNonQueryAsync());
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT committed_model_name, quality_decision_json FROM training_artifacts WHERE id = $id;";
        query.Parameters.AddWithValue("$id", artifactId.ToString());
        await using var reader = await query.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync());
        AssertEx.Equal("legacy:Q4_K_M", reader.GetString(0));
        AssertEx.True(await reader.IsDBNullAsync(1),
            "Existing promoted artifacts remain grandfathered without fabricated quality evidence.");
    }

    // The node enforces foreign keys, so a legacy row cannot hang off nothing: the schema's chain is
    // definition -> dataset -> run, with a base artifact beside it. None of it is what the migration under test changes.
    private static async Task<(Guid DatasetId, Guid RunId)> SeedParentChainAsync(SqliteConnection connection)
    {
        var definitionId = Guid.NewGuid();
        var datasetId = Guid.NewGuid();
        var baseArtifactId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await ExecuteAsync(connection,
            """
            INSERT INTO training_dataset_definitions (id, name, kind, definition_json, definition_version, version, created_at_utc, updated_at_utc)
            VALUES ($id, 'fixture', 'Chat', X'00', 1, 1, 0, 0);
            """,
            ("$id", definitionId.ToString()));

        await ExecuteAsync(connection,
            """
            INSERT INTO training_datasets (id, definition_id, definition_version, name, status, revision, content_fingerprint,
                total_sample_count, good_sample_count, bad_sample_count, rejected_sample_count, duplicate_sample_count, version, created_at_utc, updated_at_utc)
            VALUES ($id, $definition, 1, 'fixture', 'Ready', 1, $fingerprint, 1, 1, 0, 0, 0, 1, 0, 0);
            """,
            ("$id", datasetId.ToString()),
            ("$definition", definitionId.ToString()),
            ("$fingerprint", DatasetFingerprint));

        await ExecuteAsync(connection,
            """
            INSERT INTO training_base_artifacts (id, repo_id, revision, status, files_json, total_bytes, version, created_at_utc, updated_at_utc)
            VALUES ($id, 'fixture/base', 'main', 'Ready', X'00', 0, 1, 0, 0);
            """,
            ("$id", baseArtifactId.ToString()));

        await ExecuteAsync(connection,
            """
            INSERT INTO training_runs (id, dataset_id, dataset_content_fingerprint, dataset_revision, freeze_json, base_artifact_id,
                options_json, status, version, created_at_utc, updated_at_utc)
            VALUES ($id, $dataset, $fingerprint, 1, X'00', $base, X'00', 'Succeeded', 1, 0, 0);
            """,
            ("$id", runId.ToString()),
            ("$dataset", datasetId.ToString()),
            ("$fingerprint", DatasetFingerprint),
            ("$base", baseArtifactId.ToString()));

        return (datasetId, runId);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite; every value is a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }
}
