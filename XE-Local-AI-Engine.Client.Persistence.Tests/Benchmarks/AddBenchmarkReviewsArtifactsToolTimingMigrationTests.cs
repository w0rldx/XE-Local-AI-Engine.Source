namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkReviewsArtifactsToolTiming</c> creates the review and artifact tables and adds three plain columns
///     as ADD COLUMNs, never a rebuild.
/// </summary>
/// <remarks>
///     A seeded run row is read back whole and must be unchanged, with the project flag at 0 and the tool rollup NULL
///     (not measured, which is not zero calls). The rollback rebuilds a table per dropped column, so it must keep every
///     benchmark row.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddBenchmarkReviewsArtifactsToolTimingMigrationTests
{
    private const string PreviousMigrationId = "20261015121011_AddIntegrationGraphTarget";
    private const string ThisMigrationId = "20261019225908_AddBenchmarkReviewsArtifactsToolTiming";

    private static readonly Guid ProjectId = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid RunId = new("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid RevisionId = new("dddddddd-0000-0000-0000-000000000003");
    private static readonly Guid AttemptId = new("dddddddd-0000-0000-0000-000000000004");

    [Test]
    public async Task Migrate_FromThePrecedingMigration_KeepsTheSeededRunAndAddsTheColumnsUnset()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-review-artifacts-up.sqlite", PreviousMigrationId);
        await SeedAsync(probe);
        var columnsBefore = await probe.ColumnsAsync("benchmark_runs");
        var before = await RowAsync(probe, columnsBefore);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.Equal(before, await RowAsync(probe, columnsBefore), "Every pre-existing column of the run row must survive the migration unchanged.");
        AssertEx.Equal(expected: 0L, await LongAsync(probe, "SELECT requires_review FROM benchmark_projects;"),
            "A project created before review existed never required it.");
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT COUNT(*) FROM benchmark_runs WHERE tool_call_count IS NULL AND tool_time_ms IS NULL;"),
            "A legacy run reads NULL for the rollup: not measured, never zero calls.");
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT COUNT(*) FROM benchmark_judge_attempts WHERE score = 60;"), "The judge attempt is untouched.");
        AssertEx.True(await probe.TableExistsAsync("benchmark_run_reviews"));
        AssertEx.True(await probe.TableExistsAsync("benchmark_run_artifacts"));
    }

    [Test]
    public async Task Migrate_ToLatest_CreatesBothTablesWithTheirConstraintsAndIndexes()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-review-artifacts-head.sqlite");
        await SeedAsync(probe);

        AssertEx.True(await probe.IndexExistsAsync("benchmark_run_reviews", "ux_benchmark_run_reviews_run_id", unique: true, "run_id"));
        AssertEx.True(await probe.IndexExistsAsync("benchmark_run_reviews", "ix_benchmark_run_reviews_policy_revision_id", unique: false, "policy_revision_id"));
        AssertEx.True(await probe.IndexExistsAsync("benchmark_run_artifacts", "ux_benchmark_run_artifacts_attempt_criterion_kind", unique: true,
            "judge_attempt_id", "criterion_id", "kind"));
        AssertEx.True(await probe.ForeignKeyExistsAsync("benchmark_run_reviews", "run_id", "benchmark_runs"));
        AssertEx.True(await probe.ForeignKeyExistsAsync("benchmark_run_artifacts", "run_id", "benchmark_runs"));
        AssertEx.True(await probe.ForeignKeyExistsAsync("benchmark_run_artifacts", "judge_attempt_id", "benchmark_judge_attempts"));
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT \"notnull\" FROM pragma_table_info('benchmark_run_artifacts') WHERE name = 'judge_attempt_id';"),
            "judge_attempt_id is NOT NULL: a SQLite unique index repeats NULLs, so a nullable producer would admit duplicates.");
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT \"notnull\" FROM pragma_table_info('benchmark_run_artifacts') WHERE name = 'criterion_id';"),
            "criterion_id is NOT NULL for the same reason: it is the middle column of the unique index.");
        AssertEx.True(Convert.ToString(await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'benchmark_run_reviews';"),
                                 CultureInfo.InvariantCulture)!
                             .Contains("CONSTRAINT \"CK_benchmark_run_reviews_approved_score\" CHECK (state = 'rejected' OR score IS NOT NULL)", StringComparison.Ordinal),
            "The approved-needs-a-score CHECK must be in the stored table definition.");

        // The CHECKs, by behaviour: a pending row, an out-of-range score and an unknown kind are all refused by the schema itself.
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertReviewAsync(probe, "pending", score: null), "No pending row is ever stored.");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertReviewAsync(probe, "approved", score: 101));
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertReviewAsync(probe, "approved", score: null), "An approval without a score would rank nothing.");
        await InsertReviewAsync(probe, "approved", score: 100);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertReviewAsync(probe, "rejected", score: null), "One review per run.");

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertArtifactAsync(probe, "screenshot"));
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertArtifactAsync(probe, "pythonTestsStdout", criterionId: null), "criterion_id is NOT NULL.");
        await InsertArtifactAsync(probe, "pythonTestsStdout");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertArtifactAsync(probe, "pythonTestsStdout"), "One artifact per (attempt, criterion, kind).");
        await InsertArtifactAsync(probe, "pythonTestsStderr");

        AssertEx.True((await probe.AppliedMigrationsAsync(identityContext: false)).Contains(ThisMigrationId));
    }

    [Test]
    public async Task Migrate_WhenRolledBack_DropsBothTablesAndTheThreeColumnsAndKeepsTheBenchmarkRows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-review-artifacts-down.sqlite");
        await SeedAsync(probe);
        await InsertReviewAsync(probe, "approved", score: 80);
        await InsertArtifactAsync(probe, "pythonTestsStdout");

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False(await probe.TableExistsAsync("benchmark_run_reviews"));
        AssertEx.False(await probe.TableExistsAsync("benchmark_run_artifacts"));
        var runColumns = await probe.ColumnsAsync("benchmark_runs");
        AssertEx.False(runColumns.Contains("tool_call_count"));
        AssertEx.False(runColumns.Contains("tool_time_ms"));
        AssertEx.False((await probe.ColumnsAsync("benchmark_projects")).Contains("requires_review"));
        AssertEx.True(runColumns.IsSupersetOf(["segment_count", "user_score", "current_judge_attempt_id", "cell_key"]),
            "The drop rebuilds benchmark_runs; its sibling columns must come through.");
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT COUNT(*) FROM benchmark_runs WHERE user_score = 70 AND current_judge_attempt_id IS NOT NULL;"));
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT COUNT(*) FROM benchmark_projects;"));
        AssertEx.Equal(expected: 1L, await LongAsync(probe, "SELECT COUNT(*) FROM benchmark_judge_attempts;"));
    }

    private static Task SeedAsync(MigrationSchemaProbe probe) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                           VALUES ($project, 'p', X'7B7D', 4096, $agent, 1, 1, 1);
                           INSERT INTO benchmark_judge_policy_revisions (id, project_id, revision, policy_json, policy_hash, cohort_generation, comparison_set_version, created_at_utc)
                           VALUES ($revision, $project, 1, X'00', 'hash', 1, 0, 1);
                           INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint, agent_name,
                                                       agent_version, requested_context_tokens, primary_status, duration_ms, last_stream_sequence,
                                                       user_score, cell_key, version, created_at_utc, updated_at_utc)
                           VALUES ($run, $project, X'00', 'model.gguf', 'v1:aa', 'Agent', 1, 4096, 'Succeeded', 1234, 9, 70, 'run:seed', 3, 1, 2);
                           INSERT INTO benchmark_judge_attempts (id, run_id, sequence, policy_revision_id, cohort_generation, status, score, enqueued_at_utc, version)
                           VALUES ($attempt, $run, 1, $revision, 1, 'Succeeded', 60, 1, 1);
                           UPDATE benchmark_runs SET current_judge_attempt_id = $attempt WHERE id = $run;
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$project", ProjectId.ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$revision", RevisionId.ToString());
                command.Parameters.AddWithValue("$run", RunId.ToString());
                command.Parameters.AddWithValue("$attempt", AttemptId.ToString());
            });

    private static Task InsertReviewAsync(MigrationSchemaProbe probe, string state, int? score) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_run_reviews (id, run_id, state, score, version, created_at_utc, updated_at_utc)
                           VALUES ($id, $run, $state, $score, 1, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$run", RunId.ToString());
                command.Parameters.AddWithValue("$state", state);
                command.Parameters.AddWithValue("$score", score is null ? DBNull.Value : score.Value);
            });

    private static Task InsertArtifactAsync(MigrationSchemaProbe probe, string kind, string? criterionId = "c1") =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_run_artifacts (id, run_id, judge_attempt_id, criterion_id, kind, media_type, byte_count, content_hash, created_at_utc)
                           VALUES ($id, $run, $attempt, $criterion, $kind, 'text/plain; charset=utf-8', 3, 'hash', 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$run", RunId.ToString());
                command.Parameters.AddWithValue("$attempt", AttemptId.ToString());
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$criterion", criterionId is null ? DBNull.Value : criterionId);
            });

    /// <summary>The run row over exactly <paramref name="columns" />, each value <c>quote()</c>d so NULL, blobs and numbers compare as text.</summary>
    private static async Task<string> RowAsync(MigrationSchemaProbe probe, IReadOnlySet<string> columns)
    {
        // Column names come from pragma_table_info of this very table, never from input, so composing them is safe.
        var select = string.Join(" || '|' || ", columns.Order(StringComparer.Ordinal).Select(static column => $"quote(\"{column}\")"));
        return Convert.ToString(await probe.ScalarAsync($"SELECT {select} FROM benchmark_runs;"), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<long> LongAsync(MigrationSchemaProbe probe, string sql) =>
        Convert.ToInt64(await probe.ScalarAsync(sql), CultureInfo.InvariantCulture);
}
