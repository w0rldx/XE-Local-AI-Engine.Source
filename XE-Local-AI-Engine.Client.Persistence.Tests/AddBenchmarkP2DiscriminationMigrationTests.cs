namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkP2Discrimination</c> is one atomic migration: three new tables, the run's fidelity
///     projection, the project's fidelity settings, the revision's comparison-set version, and the rewritten
///     work-item CHECK. It is one migration precisely so no window exists in which a work item of a kind the old
///     CHECK forbids is written; splitting it would fail an operator's freeze rather than a test.
/// </summary>
public sealed class AddBenchmarkP2DiscriminationMigrationTests
{
    private const string PreP2MigrationId = "20260825173509_AddBenchmarkRunRepeatMode";
    private const string P2MigrationId = "20260825225103_AddBenchmarkP2Discrimination";

    [Test]
    public async Task Migrate_CreatesTheThreeTablesAndTheFidelityProjection()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-up.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("benchmark_fidelity_attempts").ConfigureAwait(false), "The migration must create benchmark_fidelity_attempts.");
        AssertEx.True(await probe.TableExistsAsync("benchmark_pairwise_fits").ConfigureAwait(false), "The migration must create benchmark_pairwise_fits.");
        AssertEx.True(await probe.TableExistsAsync("benchmark_comparisons").ConfigureAwait(false), "The migration must create benchmark_comparisons.");

        // The projection is 13 columns; counting them here keeps the documented contract and schema in step.
        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        string[] projection =
        [
            "fidelity_attempt_id",
            "perplexity_mean",
            "perplexity_std_err",
            "perplexity_chunks",
            "perplexity_context_tokens",
            "perplexity_corpus_id",
            "kld_mean",
            "kld_p99",
            "top_token_agreement",
            "kld_base_fingerprint",
            "kld_base_logits_digest",
            "fidelity_status",
            "fidelity_error_message"
        ];
        AssertEx.Equal(expected: 13, projection.Length, "The fidelity projection is 13 columns.");
        AssertEx.True(runColumns.IsSupersetOf(projection), "benchmark_runs must carry the whole fidelity projection.");

        var projectColumns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);
        AssertEx.True(projectColumns.IsSupersetOf(new[]
            {
                "fidelity_enabled",
                "fidelity_kld_enabled",
                "fidelity_chunks",
                "fidelity_kld_base_model_name",
                "fidelity_kld_base_fingerprint"
            }),
            "The migration must persist the project's fidelity settings, including which base model a KLD number is measured against.");

        var revisionColumns = await probe.ColumnsAsync("benchmark_judge_policy_revisions").ConfigureAwait(false);
        AssertEx.True(revisionColumns.Contains("comparison_set_version"), "M7 must add the comparison-set version the fit is checked against.");
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("benchmark_judge_policy_revisions", "comparison_set_version").ConfigureAwait(false),
            "An existing revision has no comparisons, so its set version starts at 0.");
    }

    /// <summary>
    ///     There are no per-run pairwise columns: a fit is ONE row with one active pointer, and ranking reads the
    ///     scores out of it. Per-run copies would let a crash mid-publication leave a ranking that blends two fits,
    ///     with every row internally consistent and the ordering wrong. This asserts they cannot come back by accident.
    /// </summary>
    [Test]
    public async Task Migrate_LeavesBenchmarkRunsWithNoPairwiseColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-no-pairwise.sqlite").ConfigureAwait(false);

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        var offenders = runColumns.Where(column => column.Contains("pairwise", StringComparison.OrdinalIgnoreCase)).ToArray();
        AssertEx.Empty(offenders);
    }

    /// <summary>
    ///     The CHECK rewrite is a SQLite table rebuild, and the queue's identity is <c>queue_sequence</c> — a FIFO
    ///     position other rows already point at. A rebuild that renumbered it would silently reorder pending work.
    /// </summary>
    [Test]
    public async Task Migrate_RebuildingTheWorkItemCheck_PreservesQueueSequenceValues()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-rebuild.sqlite", PreP2MigrationId).ConfigureAwait(false);
        await SeedProjectRunAsync(probe).ConfigureAwait(false);
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_work_items (queue_sequence, run_id, kind, judge_attempt_id, status, attempt, version, enqueued_at_utc)
                                 VALUES (41, $run, 'Primary', NULL, 'Queued', 1, 1, 100),
                                        (77, $run, 'Judge', $attempt, 'Running', 1, 1, 200);
                                 """, command =>
        {
            command.Parameters.AddWithValue("$run", RunId);
            command.Parameters.AddWithValue("$attempt", Guid.NewGuid());
        });

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        var sequences = await probe.LongsAsync("SELECT queue_sequence FROM benchmark_work_items ORDER BY queue_sequence;").ConfigureAwait(false);
        AssertEx.True(sequences.SequenceEqual([41L, 77L]), $"The rebuild must carry queue_sequence values through; got [{string.Join(", ", sequences)}].");
    }

    [Test]
    public async Task Migrate_RewrittenCheck_AcceptsAllFourKindsAndRejectsAMismatchedId()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-check.sqlite").ConfigureAwait(false);
        await SeedProjectRunAsync(probe).ConfigureAwait(false);

        await InsertWorkItemAsync(probe, 1, "Primary", judgeAttemptId: null, comparisonId: null, fidelityAttemptId: null).ConfigureAwait(false);
        await InsertWorkItemAsync(probe, 2, "Judge", judgeAttemptId: Guid.NewGuid(), comparisonId: null, fidelityAttemptId: null).ConfigureAwait(false);
        await InsertWorkItemAsync(probe, 3, "Fidelity", judgeAttemptId: null, comparisonId: null, fidelityAttemptId: Guid.NewGuid()).ConfigureAwait(false);
        await InsertWorkItemAsync(probe, 4, "Comparison", judgeAttemptId: null, comparisonId: Guid.NewGuid(), fidelityAttemptId: null).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, 5, "Comparison", judgeAttemptId: null, comparisonId: null, fidelityAttemptId: null),
                              "A Comparison item with no comparison id names nothing to execute and must be rejected.")
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, 6, "Fidelity", judgeAttemptId: null, comparisonId: null, fidelityAttemptId: null),
                              "A Fidelity item with no attempt id names nothing to measure and must be rejected.")
                          .ConfigureAwait(false);

        // Each arm names EVERY id column, so one item cannot claim to be two kinds of work at once.
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, 7, "Fidelity", judgeAttemptId: null, comparisonId: Guid.NewGuid(), fidelityAttemptId: Guid.NewGuid()),
                              "A Fidelity item carrying a comparison id must be rejected.")
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The two comparison slot indexes and the fit's active pointer index over
    ///     <c>COALESCE(task_case_id, x'00')</c> cannot be expressed by EF's <c>HasIndex().HasFilter()</c>, which takes
    ///     columns. They are raw SQL in the migration, and this reads them back from <c>sqlite_master</c> — asserting
    ///     through the EF model would pass against exactly the bare-column index that is wrong.
    /// </summary>
    [Test]
    public async Task Migrate_ExpressionIndexes_AreCoalescedAndFiltered()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-indexes.sqlite").ConfigureAwait(false);

        var slotAttempt = await IndexSqlAsync(probe, "ux_benchmark_comparisons_slot_attempt").ConfigureAwait(false);
        AssertEx.True(slotAttempt.Contains("COALESCE(task_case_id, x'00')", StringComparison.Ordinal),
            $"The per-attempt slot index must key on the COALESCE expression, not the nullable column; got: {slotAttempt}");

        var slotLive = await IndexSqlAsync(probe, "ux_benchmark_comparisons_slot_live").ConfigureAwait(false);
        AssertEx.True(slotLive.Contains("COALESCE(task_case_id, x'00')", StringComparison.Ordinal), $"The live-slot index must key on the COALESCE expression; got: {slotLive}");
        AssertEx.True(slotLive.Contains("WHERE status IN ('Queued', 'Running', 'Succeeded')", StringComparison.Ordinal),
            $"The live-slot index must be filtered on status so a failed slot can be retried; got: {slotLive}");

        var active = await IndexSqlAsync(probe, "ux_benchmark_pairwise_fits_active").ConfigureAwait(false);
        AssertEx.True(active.Contains("COALESCE(task_case_id, x'00')", StringComparison.Ordinal), $"The active-fit pointer must key on the COALESCE expression; got: {active}");
        AssertEx.True(active.Contains("WHERE is_active = 1", StringComparison.Ordinal), $"At most one ACTIVE fit per scope — the filter is the pointer; got: {active}");
    }

    [Test]
    public async Task Migrate_WhenRolledBack_RestoresTheTwoKindCheckAndDropsEverythingP2Added()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-down.sqlite").ConfigureAwait(false);

        await probe.MigrateToAsync(PreP2MigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("benchmark_fidelity_attempts").ConfigureAwait(false), "Rollback must drop benchmark_fidelity_attempts.");
        AssertEx.False(await probe.TableExistsAsync("benchmark_pairwise_fits").ConfigureAwait(false), "Rollback must drop benchmark_pairwise_fits.");
        AssertEx.False(await probe.TableExistsAsync("benchmark_comparisons").ConfigureAwait(false), "Rollback must drop benchmark_comparisons.");

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        AssertEx.False(runColumns.Contains("perplexity_mean"), "Rollback must drop the fidelity projection.");
        AssertEx.True(runColumns.Contains("repeat_mode"), "Rollback must leave the preceding migration's columns intact.");

        var workItemColumns = await probe.ColumnsAsync("benchmark_work_items").ConfigureAwait(false);
        AssertEx.False(workItemColumns.Contains("comparison_id"), "Rollback must drop comparison_id.");
        AssertEx.False(workItemColumns.Contains("fidelity_attempt_id"), "Rollback must drop fidelity_attempt_id.");
        AssertEx.True(workItemColumns.Contains("judge_attempt_id"), "Rollback must retain the original work-item schema.");

        await SeedProjectRunAsync(probe).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => probe.ExecuteAsync("""
                                                                                 INSERT INTO benchmark_work_items (queue_sequence, run_id, kind, judge_attempt_id, status, attempt, version, enqueued_at_utc)
                                                                                 VALUES (9, $run, 'Fidelity', NULL, 'Queued', 1, 1, 1);
                                                                                 """, command => command.Parameters.AddWithValue("$run", RunId)),
                              "The restored two-kind CHECK must reject a Fidelity item again.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task Migrate_RecordsThisMigrationInTheChatChain()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-p2-applied.sqlite").ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false);
        AssertEx.True(applied.Contains(P2MigrationId), "The discrimination migration must be part of the chat chain a fresh box applies.");
    }

    private static async Task<string> IndexSqlAsync(MigrationSchemaProbe probe, string indexName)
    {
        var sql = await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;",
                                 command => command.Parameters.AddWithValue("$name", indexName))
                             .ConfigureAwait(false);
        return AssertEx.NotNull(Convert.ToString(sql, CultureInfo.InvariantCulture), $"Index {indexName} must exist.");
    }

    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgentId = new("33333333-3333-3333-3333-333333333333");

    private static async Task SeedProjectRunAsync(MigrationSchemaProbe probe)
    {
        // The work item's FK needs a run, and the run's needs a project. Written as SQL rather than through the entity
        // model on purpose: the model describes the schema at head, and half these tests observe it before this migration applies.
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                                 VALUES ($project, 'p2-probe', x'00', 4096, $agent, 1, 1, 1);
                                 INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint,
                                                             agent_name, agent_version, requested_context_tokens, primary_status, last_stream_sequence,
                                                             is_warmup, version, created_at_utc, updated_at_utc)
                                 VALUES ($run, $project, x'00', 'probe-model', 'v1:0', 'probe-agent', 1, 4096, 'Queued', 0, 0, 1, 1, 1);
                                 """, command =>
        {
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$run", RunId);
            command.Parameters.AddWithValue("$agent", AgentId);
        });
    }

    private static Task InsertWorkItemAsync(MigrationSchemaProbe probe,
        long queueSequence,
        string kind,
        Guid? judgeAttemptId,
        Guid? comparisonId,
        Guid? fidelityAttemptId) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_work_items (queue_sequence, run_id, kind, judge_attempt_id, comparison_id, fidelity_attempt_id,
                                                             status, attempt, version, enqueued_at_utc)
                           VALUES ($sequence, $run, $kind, $judge, $comparison, $fidelity, 'Queued', 1, 1, 1);
                           """, command =>
        {
            command.Parameters.AddWithValue("$sequence", queueSequence);
            command.Parameters.AddWithValue("$run", RunId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$judge", (object?)judgeAttemptId ?? DBNull.Value);
            command.Parameters.AddWithValue("$comparison", (object?)comparisonId ?? DBNull.Value);
            command.Parameters.AddWithValue("$fidelity", (object?)fidelityAttemptId ?? DBNull.Value);
        });
}
