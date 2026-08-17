namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddTrainingArtifactQualityMigrationTests : IDisposable
{
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
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreQualityMigrationId);
        }

        await using (var legacyConnection = new SqliteConnection($"Data Source={path};Foreign Keys=False"))
        {
            await legacyConnection.OpenAsync();
            await using var insert = legacyConnection.CreateCommand();
            insert.CommandText = """
                INSERT INTO training_evaluation_runs
                    (id, model_name, dataset_id, dataset_content_fingerprint, membership_json, status,
                     total_count, scored_count, passed_count, version, created_at_utc, updated_at_utc)
                VALUES
                    ($id, 'legacy', $dataset, 'v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', X'00',
                     'Succeeded', 1, 1, 1, 1, 0, 0);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$dataset", Guid.NewGuid().ToString());
            AssertEx.Equal(expected: 1, await insert.ExecuteNonQueryAsync());
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.MigrateAsync();
            AssertEx.False(context.Database.HasPendingModelChanges());
        }

        await using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=False");
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
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(QualityMigrationId);
        }

        await using (var seedConnection = new SqliteConnection($"Data Source={path};Foreign Keys=False"))
        {
            await seedConnection.OpenAsync();
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = """
                INSERT INTO training_artifacts
                    (id, run_id, kind, path, sha256, size_bytes, smoke_state, smoke_reason, committed_model_name,
                     quality_comparison_id, quality_decision_json, version, created_at_utc, updated_at_utc)
                VALUES
                    ($id, $run, $kind, $path, $sha, 4, $smoke, NULL, NULL, $comparison, $decision, 3, 0, 0);
                """;
            seed.Parameters.AddWithValue("$id", artifactId.ToString());
            seed.Parameters.AddWithValue("$run", Guid.NewGuid().ToString());
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

        await using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=False");
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
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreQualityMigrationId);
        }

        await using (var seedConnection = new SqliteConnection($"Data Source={path};Foreign Keys=False"))
        {
            await seedConnection.OpenAsync();
            await using var seed = seedConnection.CreateCommand();
            seed.CommandText = """
                INSERT INTO training_artifacts
                    (id, run_id, kind, path, sha256, size_bytes, smoke_state, smoke_reason, committed_model_name,
                     version, created_at_utc, updated_at_utc)
                VALUES
                    ($id, $run, 'MergedGguf', 'legacy.gguf', $sha, 4, 'Passed', NULL, 'legacy:Q4_K_M', 3, 0, 0);
                """;
            seed.Parameters.AddWithValue("$id", artifactId.ToString());
            seed.Parameters.AddWithValue("$run", Guid.NewGuid().ToString());
            seed.Parameters.AddWithValue("$sha", new string('b', 64));
            AssertEx.Equal(expected: 1, await seed.ExecuteNonQueryAsync());
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(path, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=False");
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
}
