namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkJudgePolicies</c> makes judging repeatable: policy revisions per project, one attempt row per
///     judging, and judge work keyed by attempt instead of by run. Its Up deliberately deletes every pre-existing
///     benchmark row (operator decision C4), which is asserted here rather than trusted.
/// </summary>
public sealed class AddBenchmarkJudgePoliciesMigrationTests
{
    private const string PreviousMigration = "20260816174029_AddBenchmarkRunLaunchReceipts";
    private const string ThisMigration = "20260816200929_AddBenchmarkJudgePolicies";
    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task Migrate_ToLatest_CreatesTheJudgeTablesAndRepointsWorkItems()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("benchmark_judge_policy_revisions").ConfigureAwait(false), "Judge policy revisions must exist.");
        AssertEx.True(await probe.TableExistsAsync("benchmark_judge_attempts").ConfigureAwait(false), "Judge attempts must exist.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_judge_policy_revisions").ConfigureAwait(false)).IsSupersetOf(new[]
            {
                "id",
                "project_id",
                "revision",
                "policy_json",
                "policy_hash",
                "reference_execution_key",
                "cohort_generation",
                "created_at_utc"
            }),
            "A revision must carry its payload, its hash and its cohort state.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_judge_attempts").ConfigureAwait(false)).IsSupersetOf(new[]
            {
                "run_id",
                "sequence",
                "policy_revision_id",
                "cohort_generation",
                "judge_runtime_json",
                "judge_execution_key",
                "status",
                "result_json",
                "score",
                "launch_receipt_json",
                "environment_facts_json",
                "effective_backend",
                "placement_offloaded",
                "placement_total",
                "launch_executable_sha256",
                "launch_has_aux_assets",
                "launch_kv_cache_type_source",
                "variant",
                "kv_cache_type",
                "intended_launch_identity",
                "version"
            }),
            "An attempt must carry the whole judge-side launch-evidence block, not the run.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_work_items").ConfigureAwait(false)).Contains("judge_attempt_id"),
            "Judge work must name the attempt it judges.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false)).Contains("current_judge_attempt_id"),
            "A run must point at its current attempt.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false)).Contains("current_judge_policy_revision_id"),
            "A project must point at the policy it judges under.");

        AssertEx.True(await probe.IndexExistsAsync("benchmark_judge_policy_revisions",
                "ux_benchmark_judge_policy_revisions_project_policy_hash",
                unique: true,
                "project_id",
                "policy_hash").ConfigureAwait(false),
            "One row per distinct policy per project is what makes get-or-create deterministic.");
        AssertEx.True(await probe.IndexExistsAsync("benchmark_judge_attempts",
                "ux_benchmark_judge_attempts_run_sequence",
                unique: true,
                "run_id",
                "sequence").ConfigureAwait(false),
            "Attempt sequence must be unique within a run.");
        AssertEx.True(await probe.IndexExistsAsync("benchmark_work_items", "ux_benchmark_work_items_primary_run", unique: true, "run_id").ConfigureAwait(false),
            "The primary work index must be filtered on kind, not composite with it.");
        AssertEx.True(await probe.IndexExistsAsync("benchmark_work_items", "ux_benchmark_work_items_judge_attempt", unique: true, "judge_attempt_id").ConfigureAwait(false),
            "Judge work must be unique per attempt.");
        AssertEx.False(await probe.IndexExistsAsync("benchmark_work_items", "ux_benchmark_work_items_run_kind", unique: true, "run_id", "kind").ConfigureAwait(false),
            "The one-judge-item-per-run index must be gone; a run has one item per attempt now.");
    }

    [Test]
    public async Task Migrate_OverPopulatedRows_DeletesEveryBenchmarkRow()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-purge.sqlite", PreviousMigration).ConfigureAwait(false);
        await SeedLegacyBenchmarkRowsAsync(probe).ConfigureAwait(false);
        AssertEx.Equal(expected: 1L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_runs;").ConfigureAwait(false));

        await probe.MigrateToAsync(ThisMigration).ConfigureAwait(false);

        // Operator decision C4: the feature is unused, so the migration drops the rows rather than mapping them.
        AssertEx.Equal(expected: 0L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_work_items;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_runs;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_projects;").ConfigureAwait(false));
    }

    [Test]
    public async Task Migrate_Down_RemovesTheJudgeTablesAndRestoresTheOldWorkIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-down.sqlite").ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigration).ConfigureAwait(false);


        AssertEx.False(await probe.TableExistsAsync("benchmark_judge_attempts").ConfigureAwait(false), "Down must drop the attempts table.");
        AssertEx.False(await probe.TableExistsAsync("benchmark_judge_policy_revisions").ConfigureAwait(false), "Down must drop the revisions table.");
        AssertEx.False((await probe.ColumnsAsync("benchmark_work_items").ConfigureAwait(false)).Contains("judge_attempt_id"), "Down must drop the attempt pointer.");
        AssertEx.True(await probe.IndexExistsAsync("benchmark_work_items", "ux_benchmark_work_items_run_kind", unique: true, "run_id", "kind").ConfigureAwait(false),
            "Down must restore the previous uniqueness rule.");
    }

    [Test]
    public async Task UserScoreCheck_AcceptsTheWholeZeroToHundredRangeAndRefusesAbove()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-user-score.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunAsync(probe).ConfigureAwait(false);

        await probe.ExecuteAsync("UPDATE benchmark_runs SET user_score = 0;").ConfigureAwait(false);
        await probe.ExecuteAsync("UPDATE benchmark_runs SET user_score = 100;").ConfigureAwait(false);
        await probe.ExecuteAsync("UPDATE benchmark_runs SET user_score = NULL;").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => probe.ExecuteAsync("UPDATE benchmark_runs SET user_score = 101;"),
            "The operator score is 0..100 now, and 101 is not a score.");
    }

    [Test]
    public async Task AttemptChecks_BoundTheScoreAndTheStatusVocabulary()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-attempt-checks.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunAsync(probe).ConfigureAwait(false);
        var revisionId = await SeedRevisionAsync(probe, "policy-hash-a").ConfigureAwait(false);

        await InsertAttemptAsync(probe, Guid.NewGuid(), revisionId, sequence: 1, status: "Succeeded", score: 0).ConfigureAwait(false);
        await InsertAttemptAsync(probe, Guid.NewGuid(), revisionId, sequence: 2, status: "Succeeded", score: 100).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertAttemptAsync(probe, Guid.NewGuid(), revisionId, sequence: 3, status: "Succeeded", score: 101),
            "An attempt score above 100 is not representable.");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertAttemptAsync(probe, Guid.NewGuid(), revisionId, sequence: 4, status: "Pending", score: null),
            "Pending is a run-level state, never an attempt state.");
    }

    [Test]
    public async Task WorkItemChecks_TieTheAttemptPointerToTheKind()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-work-checks.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunAsync(probe).ConfigureAwait(false);
        var revisionId = await SeedRevisionAsync(probe, "policy-hash-a").ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        await InsertAttemptAsync(probe, attemptId, revisionId, sequence: 1, status: "Queued", score: null).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, "Judge", judgeAttemptId: null),
            "Judge work without an attempt is not representable.");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, "Primary", attemptId),
            "Primary work must not carry an attempt pointer.");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, "Primary", judgeAttemptId: null, attempt: 2),
            "Retries are new attempts, never a second try of the same work item.");
    }

    [Test]
    public async Task FilteredUniqueIndexes_ConstrainPrimaryPerRunAndJudgePerAttempt()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-filtered-indexes.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunAsync(probe).ConfigureAwait(false);
        var revisionId = await SeedRevisionAsync(probe, "policy-hash-a").ConfigureAwait(false);
        var firstAttemptId = Guid.NewGuid();
        var secondAttemptId = Guid.NewGuid();
        await InsertAttemptAsync(probe, firstAttemptId, revisionId, sequence: 1, status: "Succeeded", score: 60).ConfigureAwait(false);
        await InsertAttemptAsync(probe, secondAttemptId, revisionId, sequence: 2, status: "Queued", score: null).ConfigureAwait(false);

        await InsertWorkItemAsync(probe, "Primary", judgeAttemptId: null).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, "Primary", judgeAttemptId: null),
            "A run has exactly one primary work item.");

        await InsertWorkItemAsync(probe, "Judge", firstAttemptId).ConfigureAwait(false);
        // The point of the filtered index: a second judge item is fine, a second item for the SAME attempt is not.
        await InsertWorkItemAsync(probe, "Judge", secondAttemptId).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertWorkItemAsync(probe, "Judge", firstAttemptId),
            "Two work items for one attempt would let the same judging be claimed twice.");
    }

    [Test]
    public async Task RevisionHashIndex_RefusesASecondRowForTheSamePolicy()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-judge-policies-hash-unique.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunAsync(probe).ConfigureAwait(false);
        _ = await SeedRevisionAsync(probe, "policy-hash-a").ConfigureAwait(false);
        _ = await SeedRevisionAsync(probe, "policy-hash-b", revision: 2).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => SeedRevisionAsync(probe, "policy-hash-a", revision: 3),
            "Re-activating an old policy must repoint at its revision, never mint a duplicate.");
    }

    private static async Task SeedLegacyBenchmarkRowsAsync(MigrationSchemaProbe probe)
    {
        await SeedProjectAndRunAsync(probe, atHead: false).ConfigureAwait(false);
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_work_items (run_id, kind, status, attempt, version, enqueued_at_utc)
                                 VALUES ($run, 'Primary', 'Succeeded', 1, 2, 1);
                                 """,
            command => command.Parameters.AddWithValue("$run", RunId.ToString())).ConfigureAwait(false);
    }

    /// <param name="atHead">
    ///     False seeds the shape the chain had before the judge columns were dropped — the only shape the purge test can
    ///     write, because it seeds at this migration's predecessor.
    /// </param>
    private static async Task SeedProjectAndRunAsync(MigrationSchemaProbe probe, bool atHead = true)
    {
        await probe.ExecuteAsync(atHead
                ? """
                  INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                  VALUES ($id, 'Legacy', X'00', 4096, $agent, 1, 1, 1);
                  """
                : """
                  INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, judge_enabled,
                                                  judge_prompt_version, judge_output_schema_version, version, created_at_utc, updated_at_utc)
                  VALUES ($id, 'Legacy', X'00', 4096, $agent, 1, 1, 1, 1, 1, 1);
                  """,
            command =>
            {
                command.Parameters.AddWithValue("$id", ProjectId.ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
            }).ConfigureAwait(false);
        await probe.ExecuteAsync(atHead
                ? """
                  INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint, agent_name,
                                              agent_version, requested_context_tokens, primary_status, last_stream_sequence,
                                              version, created_at_utc, updated_at_utc)
                  VALUES ($id, $project, X'00', 'model.gguf', 'v1:aa', 'Agent', 1, 4096, 'Succeeded', 0, 1, 1, 1);
                  """
                : """
                  INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint, agent_name,
                                              agent_version, requested_context_tokens, primary_status, last_stream_sequence, judge_status,
                                              version, created_at_utc, updated_at_utc)
                  VALUES ($id, $project, X'00', 'model.gguf', 'v1:aa', 'Agent', 1, 4096, 'Succeeded', 0, 'Pending', 1, 1, 1);
                  """,
            command =>
            {
                command.Parameters.AddWithValue("$id", RunId.ToString());
                command.Parameters.AddWithValue("$project", ProjectId.ToString());
            }).ConfigureAwait(false);
    }

    private static async Task<Guid> SeedRevisionAsync(MigrationSchemaProbe probe, string policyHash, int revision = 1)
    {
        var revisionId = Guid.NewGuid();
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_judge_policy_revisions (id, project_id, revision, policy_json, policy_hash, cohort_generation, created_at_utc)
                                 VALUES ($id, $project, $revision, X'00', $hash, 1, 1);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", revisionId.ToString());
                command.Parameters.AddWithValue("$project", ProjectId.ToString());
                command.Parameters.AddWithValue("$revision", revision);
                command.Parameters.AddWithValue("$hash", policyHash);
            }).ConfigureAwait(false);
        return revisionId;
    }

    private static Task InsertAttemptAsync(MigrationSchemaProbe probe,
        Guid attemptId,
        Guid revisionId,
        int sequence,
        string status,
        int? score) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_judge_attempts (id, run_id, sequence, policy_revision_id, cohort_generation, status, score, enqueued_at_utc, version)
                           VALUES ($id, $run, $sequence, $revision, 1, $status, $score, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", attemptId.ToString());
                command.Parameters.AddWithValue("$run", RunId.ToString());
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$revision", revisionId.ToString());
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$score", score is null ? DBNull.Value : score.Value);
            });

    private static Task InsertWorkItemAsync(MigrationSchemaProbe probe, string kind, Guid? judgeAttemptId, int attempt = 1) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_work_items (run_id, kind, judge_attempt_id, status, attempt, version, enqueued_at_utc)
                           VALUES ($run, $kind, $attemptId, 'Queued', $attempt, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$run", RunId.ToString());
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$attemptId", judgeAttemptId is null ? DBNull.Value : judgeAttemptId.Value.ToString());
                command.Parameters.AddWithValue("$attempt", attempt);
            });
}
