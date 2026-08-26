namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The invariants P2's new tables enforce in the DATABASE rather than in the code that writes them: the canonical
///     pair ordering, one live comparison per slot with retries still possible, and exactly one active Bradley–Terry
///     fit per scope. Each is a rule a careful publisher would also keep — and each is here because "the publisher was
///     careful" is not an invariant, it is a hope.
/// </summary>
public sealed class BenchmarkP2SchemaConstraintTests
{
    private static readonly Guid ProjectId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid RevisionId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid RunA = new("11111111-0000-0000-0000-000000000000");
    private static readonly Guid RunB = new("22222222-0000-0000-0000-000000000000");

    [Test]
    public async Task Comparison_NonCanonicalPairOrder_IsRejectedByTheCheck()
    {
        await using var probe = await SeedAsync("p2-pair-order.sqlite").ConfigureAwait(false);

        await InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 1, status: "Queued").ConfigureAwait(false);

        // Reversed, the SAME comparison would occupy a second slot no index could join to the first.
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertComparisonAsync(probe, Guid.NewGuid(), RunB, RunA, order: 0, attemptSequence: 1, status: "Queued"),
                              "run_a_id must be the smaller id — the canonical ordering is a database invariant, not a planner convention.")
                          .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 2, attemptSequence: 1, status: "Queued"),
                              "Only the two presentation orders exist; position swap is the whole point.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task Comparison_SecondLiveRowForOneSlot_IsRejected()
    {
        await using var probe = await SeedAsync("p2-slot-live.sqlite").ConfigureAwait(false);

        await InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 1, status: "Queued").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 2, status: "Queued"),
                              "One live-or-successful comparison per slot: two would both be fitted and the pair would count twice.")
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The live-slot index is filtered on status precisely so a failed or cancelled comparison can be re-enqueued.
    ///     A single unfiltered unique index would make the recovery path in the plan impossible to implement.
    /// </summary>
    [Test]
    public async Task Comparison_FailedSlot_CanBeReEnqueuedAtTheNextAttemptSequence()
    {
        await using var probe = await SeedAsync("p2-slot-retry.sqlite").ConfigureAwait(false);

        await InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 1, status: "Failed").ConfigureAwait(false);
        await InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 2, status: "Cancelled").ConfigureAwait(false);
        await InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 3, status: "Queued").ConfigureAwait(false);

        var live = await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_comparisons WHERE status IN ('Queued', 'Running', 'Succeeded');").ConfigureAwait(false);
        AssertEx.Equal(expected: 1L, Convert.ToInt64(live, CultureInfo.InvariantCulture), "Exactly one attempt is live after two terminal failures.");

        // The per-attempt index still separates them, so the history of the slot survives the retries.
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertComparisonAsync(probe, Guid.NewGuid(), RunA, RunB, order: 0, attemptSequence: 1, status: "Failed"),
                              "Re-using an attempt sequence would overwrite a slot's history.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task PairwiseFit_SecondActiveRowInOneScope_IsRejectedByTheIndex()
    {
        await using var probe = await SeedAsync("p2-fit-pointer.sqlite").ConfigureAwait(false);

        await InsertFitAsync(probe, "v1:aaa", isActive: true).ConfigureAwait(false);
        await InsertFitAsync(probe, "v1:bbb", isActive: false).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertFitAsync(probe, "v1:ccc", isActive: true),
                              "At most one active fit per (revision, generation, case) — a second one is a ranking that blends two fits.")
                          .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertFitAsync(probe, "v1:aaa", isActive: false),
                              "The fit key is unique, so a duplicate publication violates and no-ops rather than minting a second fit of the same thing.")
                          .ConfigureAwait(false);
    }

    private static async Task<MigrationSchemaProbe> SeedAsync(string fileName)
    {
        var probe = await MigrationSchemaProbe.MigrateChatAsync(fileName).ConfigureAwait(false);
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                                 VALUES ($project, 'p2-constraints', x'00', 4096, $agent, 1, 1, 1);
                                 INSERT INTO benchmark_judge_policy_revisions (id, project_id, revision, policy_json, policy_hash, cohort_generation, comparison_set_version, created_at_utc)
                                 VALUES ($revision, $project, 1, x'00', 'hash', 1, 0, 1);
                                 """, command =>
        {
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$revision", RevisionId);
            command.Parameters.AddWithValue("$agent", Guid.NewGuid());
        });
        return probe;
    }

    private static Task InsertComparisonAsync(MigrationSchemaProbe probe,
        Guid id,
        Guid runAId,
        Guid runBId,
        int order,
        int attemptSequence,
        string status) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_comparisons (id, project_id, policy_revision_id, cohort_generation, task_case_id, task_input_hash,
                                                              run_a_id, run_b_id, "order", attempt_sequence, sequence, status,
                                                              answer_a_truncated, answer_b_truncated, enqueued_at_utc, version)
                           VALUES ($id, $project, $revision, 1, NULL, '', $runA, $runB, $order, $attemptSequence, $attemptSequence, $status, 0, 0, 1, 1);
                           """, command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$revision", RevisionId);
            command.Parameters.AddWithValue("$runA", runAId);
            command.Parameters.AddWithValue("$runB", runBId);
            command.Parameters.AddWithValue("$order", order);
            command.Parameters.AddWithValue("$attemptSequence", attemptSequence);
            command.Parameters.AddWithValue("$status", status);
        });

    private static Task InsertFitAsync(MigrationSchemaProbe probe, string fitKey, bool isActive) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_pairwise_fits (id, project_id, policy_revision_id, cohort_generation, task_case_id, fit_key,
                                                                judge_execution_key, comparison_set_version, fitted_set_json, scores_json,
                                                                iterations, bootstrap_replicates, is_active, created_at_utc, version)
                           VALUES ($id, $project, $revision, 1, NULL, $fitKey, 'exec', 1, '[]', '[]', 12, 1000, $isActive, 1, 1);
                           """, command =>
        {
            command.Parameters.AddWithValue("$id", Guid.NewGuid());
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$revision", RevisionId);
            command.Parameters.AddWithValue("$fitKey", fitKey);
            command.Parameters.AddWithValue("$isActive", isActive ? 1 : 0);
        });
}
