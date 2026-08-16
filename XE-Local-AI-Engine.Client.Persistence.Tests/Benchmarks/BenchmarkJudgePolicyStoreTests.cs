namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The judge-policy and judge-attempt store surface: activation and its cohort reset, the enqueue rules, the
///     attempt that must land inside the primary-success transaction, deletion order, recovery and the reference-key
///     compare-and-swap.
/// </summary>
public sealed class BenchmarkJudgePolicyStoreTests : IDisposable
{
    private const string HashA = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string HashB = "0000000000000000000000000000000000000000000000000000000000000002";
    private static readonly byte[] PolicyA = Encoding.UTF8.GetBytes("""{"rubric":"a"}""");
    private static readonly byte[] PolicyB = Encoding.UTF8.GetBytes("""{"rubric":"b"}""");
    private static readonly byte[] JudgeRuntime = Encoding.UTF8.GetBytes("""{"judgeRuntime":1}""");

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ActivatePolicy_CreatesRevisionOneAndPointsTheProjectAtIt()
    {
        await using var context = await CreateDatabaseAsync("activate-first.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);

        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);

        AssertEx.True(activation.WasCreated, "The first activation must create the revision.");
        AssertEx.Equal(expected: 1, activation.Revision.Revision);
        AssertEx.Equal(expected: 1, activation.Revision.CohortGeneration);
        AssertEx.Null(activation.Revision.ReferenceExecutionKey, "A fresh cohort has no reference key yet.");
        AssertEx.Empty(activation.SucceededRunIds);
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false));
        AssertEx.Equal(activation.Revision.Id, current.Id);
        AssertEx.True(current.PolicyJson is not null, "The current revision must carry its payload.");
        AssertBytes(PolicyA, current.PolicyJson!.Value.Span);
        var listed = await store.ListJudgePolicyRevisionsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Null(listed[0].PolicyJson, "Listing revisions must not decrypt one policy blob per row.");
    }

    [Test]
    public async Task ActivatePolicy_SameHash_IsANoOpThatDoesNotResetTheCohort()
    {
        await using var context = await CreateDatabaseAsync("activate-noop.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var first = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(first.Revision.Id, first.Revision.CohortGeneration, "key-a").ConfigureAwait(false));
        var afterFirst = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));

        var repeated = await store.ActivateJudgePolicyAsync(project.Id, afterFirst.Version, PolicyA, HashA).ConfigureAwait(false);

        AssertEx.False(repeated.WasCreated, "Re-activating the current policy creates nothing.");
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false));
        AssertEx.Equal("key-a", current.ReferenceExecutionKey);
        AssertEx.Equal(expected: 1, current.CohortGeneration);
        AssertEx.Equal(afterFirst.Version, AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)).Version);
    }

    [Test]
    public async Task ActivatePolicy_NewThenOldHash_ReusesTheOriginalRevisionAndResetsItsCohort()
    {
        await using var context = await CreateDatabaseAsync("activate-reuse.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var first = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(first.Revision.Id, first.Revision.CohortGeneration, "key-a").ConfigureAwait(false));

        var second = await store.ActivateJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id).ConfigureAwait(false), PolicyB, HashB).ConfigureAwait(false);
        AssertEx.True(second.WasCreated);
        AssertEx.Equal(expected: 2, second.Revision.Revision);
        AssertEx.Null(second.Revision.ReferenceExecutionKey, "A new revision starts its own, open cohort.");

        var back = await store.ActivateJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id).ConfigureAwait(false), PolicyA, HashA).ConfigureAwait(false);

        AssertEx.False(back.WasCreated, "Returning to a policy the project has held before must reuse its revision.");
        AssertEx.Equal(first.Revision.Id, back.Revision.Id);
        AssertEx.Equal(expected: 1, back.Revision.Revision);
        AssertEx.Null(back.Revision.ReferenceExecutionKey, "Every activation resets the cohort, reuse included.");
        AssertEx.Equal(expected: 2, back.Revision.CohortGeneration);
        AssertEx.Equal(expected: 2, (await store.ListJudgePolicyRevisionsAsync(project.Id).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task DisablePolicy_ClearsThePointerAndKeepsTheRevisionHistory()
    {
        await using var context = await CreateDatabaseAsync("disable.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        _ = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);

        await store.DisableJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id).ConfigureAwait(false)).ConfigureAwait(false);

        AssertEx.Null(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false), "Disabling clears the pointer.");
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(project.Id).ConfigureAwait(false)).Count, "Revisions are history and stay.");
    }

    [Test]
    public async Task ActivateAndDisable_WhileAnAttemptIsActive_AreRefusedWithoutWritingAnything()
    {
        await using var context = await CreateDatabaseAsync("activate-blocked.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        _ = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);

        var version = await CurrentVersionAsync(store, project.Id).ConfigureAwait(false);
        var queued = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.ActivateJudgePolicyAsync(project.Id, version, PolicyB, HashB)).ConfigureAwait(false);
        AssertEx.Equal("JudgeAttemptsActive", queued.Code);
        var disabled = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.DisableJudgePolicyAsync(project.Id, version)).ConfigureAwait(false);
        AssertEx.Equal("JudgeAttemptsActive", disabled.Code);

        // A refused activation must be atomic: no half-created revision two, no moved pointer, no version bump.
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(project.Id).ConfigureAwait(false)).Count);
        AssertEx.Equal(revision.Id, AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).Id);
        AssertEx.Equal(version, await CurrentVersionAsync(store, project.Id).ConfigureAwait(false));

        // Running, not just queued, is equally blocking.
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var running = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.DisableJudgePolicyAsync(project.Id, version)).ConfigureAwait(false);
        AssertEx.Equal("JudgeAttemptsActive", running.Code);
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithACurrentPolicy_InsertsAttemptOneAndItsWorkItemAtomically()
    {
        await using var context = await CreateDatabaseAsync("attempt-one.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);

        var attempts = await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, attempts.Count);
        AssertEx.Equal(expected: 1, attempts[0].Sequence);
        AssertEx.Equal(revision.Id, attempts[0].PolicyRevisionId);
        AssertEx.Equal(revision.CohortGeneration, attempts[0].CohortGeneration);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Queued, attempts[0].Status);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Judge, claimed.Kind);
        AssertEx.Equal(attempts[0].Id, RequireAttemptId(claimed));
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithoutACurrentPolicy_InsertsNoAttemptAndQueuesNoJudgeWork()
    {
        await using var context = await CreateDatabaseAsync("attempt-none.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version)).ConfigureAwait(false);

        AssertEx.Empty(await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false), "There is no policy to judge under.");
        _ = succeeded;
        AssertEx.Null(await store.ClaimNextAsync().ConfigureAwait(false), "No attempt means no claimable judge work.");
    }

    [Test]
    public async Task MarkPrimarySucceeded_WhenThePolicyMovedUnderTheRun_RollsBackAndThrows()
    {
        await using var context = await CreateDatabaseAsync("attempt-policy-changed.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<BenchmarkJudgePolicyChangedException>(() =>
                              store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
                              {
                                  JudgeAttempt = new BenchmarkJudgeAttemptSeed(Guid.NewGuid(), new ReadOnlyMemory<byte>(JudgeRuntime))
                              }))
                          .ConfigureAwait(false);

        // Rolled back with the transaction: primary success must not commit against a policy that is no longer current.
        var reloaded = AssertEx.NotNull(await store.GetRunAsync(run.Id).ConfigureAwait(false));
        AssertEx.Equal(BenchmarkPrimaryStatus.Running, reloaded.PrimaryStatus);
        AssertEx.Empty(await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false));
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithoutAResolvedJudgeRuntime_InsertsAFailedAttemptAndATerminalWorkItem()
    {
        await using var context = await CreateDatabaseAsync("attempt-unresolved.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, RuntimeJson: null, "judge runtime unresolved")
        }).ConfigureAwait(false);

        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, succeeded.PrimaryStatus);
        var attempts = await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, attempts.Count);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, attempts[0].Status);
        AssertEx.Equal("judge runtime unresolved", attempts[0].ErrorMessage);
        AssertEx.Null(attempts[0].JudgeRuntimeJson, "A pre-resolution failure has no runtime to store.");

        // The work item exists so the attempt/work-item invariant holds, but it is terminal: never claimable, and it
        // must not sit in front of the next run's primary work.
        var second = await store.StartRunAsync(NewRun(AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)))).ConfigureAwait(false);
        var next = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(second.Id, next.RunId);
        AssertEx.Equal(BenchmarkWorkKind.Primary, next.Kind);
    }

    [Test]
    public async Task EnqueueJudgeAttempt_AppliesEveryRefusalRuleAndForceBypassesTheAlreadyAppliedGuard()
    {
        await using var context = await CreateDatabaseAsync("enqueue-rules.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);

        // The run's first attempt is still queued.
        var active = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, run.Version, revision.Id))).ConfigureAwait(false);
        AssertEx.Equal("JudgeAttemptActive", active.Code);

        // A revision that is not the project's current one is a retryable policy change, not a validation error.
        _ = await AssertEx.ThrowsAsync<BenchmarkJudgePolicyChangedException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, run.Version, Guid.NewGuid())))
                          .ConfigureAwait(false);

        var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Succeeded, (await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false))[0].Status);

        // Not ranked yet: no execution key, so re-judging is allowed and inserts attempt two.
        var second = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, judged.Version, revision.Id)).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, second.Sequence);

        var secondJudge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var secondJudged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, secondJudge.Version, Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);

        // S1 writes the key at launch readiness; here it is set directly so the ranked-cohort guard can be exercised.
        await SetExecutionKeyAsync(context, second.Id, "key-a").ConfigureAwait(false);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a").ConfigureAwait(false));

        var applied = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, secondJudged.Version, revision.Id)))
                                    .ConfigureAwait(false);
        AssertEx.Equal("JudgePolicyAlreadyApplied", applied.Code);

        var forced = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, secondJudged.Version, revision.Id) with
        {
            Force = true
        }).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, forced.Sequence);
        AssertEx.Equal(expected: 3, (await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false)).Count, "Every judging is its own row; nothing is overwritten.");
    }

    [Test]
    public async Task EnqueueJudgeAttempt_WhenTheCohortGenerationMoved_IsAllowedWithoutForce()
    {
        await using var context = await CreateDatabaseAsync("enqueue-stale-generation.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);
        await SetExecutionKeyAsync(context, RequireAttemptId(judge), "key-a").ConfigureAwait(false);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a").ConfigureAwait(false));

        // Back to the same policy: the revision is reused but the cohort generation moves, so the ranked attempt is
        // stale and a plain re-judge must go through without force.
        var reactivated = await store.ActivateJudgePolicyAsync(project.Id,
            await CurrentVersionAsync(store, project.Id).ConfigureAwait(false),
            PolicyB,
            HashB).ConfigureAwait(false);
        var backToA = await store.ActivateJudgePolicyAsync(project.Id,
            await CurrentVersionAsync(store, project.Id).ConfigureAwait(false),
            PolicyA,
            HashA).ConfigureAwait(false);
        AssertEx.Equal(revision.Id, backToA.Revision.Id);
        AssertEx.Equal(expected: 2, backToA.Revision.CohortGeneration);
        AssertEx.Equal(expected: 2, reactivated.Revision.Revision);

        var next = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, judged.Version, revision.Id)).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, next.Sequence);
        AssertEx.Equal(expected: 2, next.CohortGeneration, "A new attempt is stamped with the live generation.");
    }

    [Test]
    public async Task SetUserScore_AcceptsTheWholeRangeAndClearsWithNull()
    {
        await using var context = await CreateDatabaseAsync("user-score.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version)).ConfigureAwait(false);

        var zero = await store.SetUserScoreAsync(run.Id, score: 0, succeeded.Version).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, zero.UserScore);
        var hundred = await store.SetUserScoreAsync(run.Id, score: 100, zero.Version).ConfigureAwait(false);
        AssertEx.Equal(expected: 100, hundred.UserScore);
        var cleared = await store.SetUserScoreAsync(run.Id, score: null, hundred.Version).ConfigureAwait(false);
        AssertEx.Null(cleared.UserScore, "Null clears the operator override.");

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.SetUserScoreAsync(run.Id, score: 101, cleared.Version)).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteRun_RemovesWorkAttemptsAndTheRunAndResetsTheCohortWhenItWasTheLast()
    {
        var databasePath = GetDatabasePath("delete-order.sqlite");
        Guid projectId;
        await using (var context = await CreateDatabaseAsync(databasePath, create: true).ConfigureAwait(false))
        {
            var store = new BenchmarkStore(context, TimeProvider.System);
            var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
            projectId = project.Id;
            var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);
            var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
            var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);
            await SetExecutionKeyAsync(context, RequireAttemptId(judge), "key-a").ConfigureAwait(false);
            AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a").ConfigureAwait(false));

            await store.DeleteRunAsync(run.Id, judged.Version).ConfigureAwait(false);

            // Deleting the project's last run leaves a cohort nothing is measured against; it reopens.
            var afterDelete = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(projectId).ConfigureAwait(false));
            AssertEx.Null(afterDelete.ReferenceExecutionKey);
            AssertEx.Equal(expected: 2, afterDelete.CohortGeneration);

            await store.DeleteProjectAsync(projectId, await CurrentVersionAsync(store, projectId).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // Foreign keys off is the real node configuration, so nothing catches an orphan for us.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            _ = await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_work_items;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_judge_attempts;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_runs;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_judge_policy_revisions;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_projects;").ConfigureAwait(false));
    }

    [Test]
    public async Task RecoverOnStartup_MarksRunningAttemptsFailedWithoutTouchingResults()
    {
        await using var context = await CreateDatabaseAsync("recover-attempts.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Running, (await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false))[0].Status);

        _ = await store.RecoverRunsOnStartupAsync().ConfigureAwait(false);

        var attempts = await ReadAttemptsAsync(context, run.Id).ConfigureAwait(false);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, attempts[0].Status);
        AssertEx.Equal("Interrupted by application restart.", attempts[0].ErrorMessage);
        AssertEx.Null(attempts[0].ResultJson, "Recovery never invents a result.");
        _ = judge;
    }

    [Test]
    public async Task JudgeSuccess_PromotesTheCohortOnTheFirstSuccess_NotAtReadiness()
    {
        await using var context = await CreateDatabaseAsync("promote-on-success.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);

        // The first attempt reaches readiness under runtime A and then fails. A failed judging must not define the
        // cohort, or one bad launch would exclude every later run that ran correctly on a different runtime.
        var first = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkJudgeLaunchReadyAsync(RequireAttemptId(first), first.QueueSequence, first.Version, Receipt(), "runtime-a").ConfigureAwait(false);
        var failed = await store.MarkJudgeFailedAsync(run.Id, first.Version, "judge blew up").ConfigureAwait(false);
        AssertEx.Null(AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).ReferenceExecutionKey,
            "A failed first attempt must leave the cohort open.");

        var second = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, failed.Version, revision.Id)).ConfigureAwait(false);
        var secondWork = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkJudgeLaunchReadyAsync(second.Id, secondWork.QueueSequence, secondWork.Version, Receipt(), "runtime-b").ConfigureAwait(false);
        _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, secondWork.Version, Encoding.UTF8.GetBytes("{}"), 5, 73))
                       .ConfigureAwait(false);

        AssertEx.Equal("runtime-b", AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).ReferenceExecutionKey,
            "The first SUCCESS of the live generation defines the cohort.");
        var persisted = AssertEx.NotNull(await store.GetJudgeAttemptAsync(second.Id).ConfigureAwait(false));
        AssertEx.Equal<int?>(73, persisted.Score);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Succeeded, persisted.Status);
    }

    [Test]
    public async Task RunJudgeView_ReportsWhyARunIsNotRanked()
    {
        await using var context = await CreateDatabaseAsync("judge-view.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await SucceedRunAsync(store, project, revision).ConfigureAwait(false);

        var queued = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id).ConfigureAwait(false)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Queued, queued.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgePending, queued.RankExclusionReason);
        AssertEx.True(queued.PolicyCurrent, "The attempt was enqueued under the project's current revision.");
        AssertEx.False(queued.ExecutionCurrent, "Nothing has claimed the cohort yet.");

        var work = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, work.Version, Encoding.UTF8.GetBytes("{}"), 5, 61))
                                .ConfigureAwait(false);

        // Succeeded and scored, but the launch never produced an execution key, so it can never be ranked.
        var incomplete = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id).ConfigureAwait(false)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Succeeded, incomplete.State);
        AssertEx.Equal<int?>(61, incomplete.Score);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonExecutionIdentityIncomplete, incomplete.RankExclusionReason);

        // An operator score always ranks, whatever the judge did.
        var scored = await store.SetUserScoreAsync(run.Id, score: 88, judged.Version).ConfigureAwait(false);
        var ranked = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id).ConfigureAwait(false)).Judge);
        AssertEx.Null(ranked.RankExclusionReason, "An operator override is always part of the ranking.");
        AssertEx.Equal<int?>(88, scored.UserScore);

        // A run with no attempt at all is `none`, and unscored.
        var plainProject = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var plainRun = await store.StartRunAsync(NewRun(plainProject)).ConfigureAwait(false);
        var none = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(plainRun.Id).ConfigureAwait(false)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, none.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonNoScore, none.RankExclusionReason);
    }

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new("{}", "{}", new string('e', count: 64), new string('r', count: 64), "identity", "cpu", null, null,
            new string('x', count: 64), false, "auto");

    [Test]
    public async Task TryPromoteReferenceExecutionKey_PromotesOnceAndRefusesStaleGenerations()
    {
        await using var context = await CreateDatabaseAsync("promote-cas.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);
        var revision = activation.Revision;

        AssertEx.False(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration + 1, "key-a").ConfigureAwait(false),
            "An attempt stamped with another generation must never define the cohort.");
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a").ConfigureAwait(false));
        AssertEx.False(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-b").ConfigureAwait(false),
            "The reference key is insert-if-null; the first same-generation success owns it.");
        AssertEx.Equal("key-a", AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).ReferenceExecutionKey);
    }

    private static Guid RequireAttemptId(BenchmarkClaimedWork work)
    {
        AssertEx.True(work.JudgeAttemptId is not null, "Claimed judge work must name the attempt it judges.");
        return work.JudgeAttemptId!.Value;
    }

    private static async Task<long> CurrentVersionAsync(BenchmarkStore store, Guid projectId) =>
        AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false)).Version;

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite, no interpolation.
        command.CommandText = sql;
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<IReadOnlyList<BenchmarkJudgeAttempt>> ReadAttemptsAsync(NodeChatDbContext context, Guid runId)
    {
        context.ChangeTracker.Clear();
        return await context.BenchmarkJudgeAttempts.AsNoTracking()
                            .Where(entity => entity.RunId == runId)
                            .OrderBy(entity => entity.Sequence)
                            .ToArrayAsync()
                            .ConfigureAwait(false);
    }

    private static async Task SetExecutionKeyAsync(NodeChatDbContext context, Guid attemptId, string executionKey)
    {
        context.ChangeTracker.Clear();
        _ = await context.BenchmarkJudgeAttempts.Where(entity => entity.Id == attemptId)
                         .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.JudgeExecutionKey, executionKey))
                         .ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision)> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)), activation.Revision);
    }

    private static async Task<BenchmarkRunRecord> SucceedRunAsync(BenchmarkStore store,
        BenchmarkProjectRecord project,
        BenchmarkJudgePolicyRevisionRecord revision)
    {
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        return await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, new ReadOnlyMemory<byte>(JudgeRuntime))
        }).ConfigureAwait(false);
    }

    private static BenchmarkPrimarySuccessCommand PrimarySuccess(Guid runId, long expectedWorkVersion) =>
        new(runId, expectedWorkVersion, Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""), 1, 4096, 10, 12, 120);

    private static BenchmarkEnqueueJudgeAttemptCommand Enqueue(Guid runId, long expectedRunVersion, Guid revisionId) =>
        new(runId, expectedRunVersion, revisionId, new ReadOnlyMemory<byte>(JudgeRuntime));

    private static BenchmarkProjectInput NewProject(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("""{"task":"answer"}"""), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""), "model.gguf",
            LocalModelOrigin.Imported, "v1:" + new string('a', count: 64), "Agent", 1, 4096);

    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        AssertEx.True(actual.SequenceEqual(expected), "Byte payload should round-trip exactly.");

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName) =>
        await CreateDatabaseAsync(GetDatabasePath(fileName), create: true).ConfigureAwait(false);

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string databasePath, bool create)
    {
        var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        if (create)
        {
            _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return context;
    }

    private string GetDatabasePath(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
