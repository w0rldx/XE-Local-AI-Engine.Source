namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
[Category(TestCategories.Integration)]
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
        await using var context = await CreateDatabaseAsync("activate-first.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());

        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);

        AssertEx.True(activation.WasCreated, "The first activation must create the revision.");
        AssertEx.Equal(expected: 1, activation.Revision.Revision);
        AssertEx.Equal(expected: 1, activation.Revision.CohortGeneration);
        AssertEx.Null(activation.Revision.ReferenceExecutionKey, "A fresh cohort has no reference key yet.");
        AssertEx.Empty(activation.SucceededRunIds);
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id));
        AssertEx.Equal(activation.Revision.Id, current.Id);
        AssertEx.True(current.PolicyJson is not null, "The current revision must carry its payload.");
        AssertBytes(PolicyA, current.PolicyJson!.Value.Span);
        var listed = await store.ListJudgePolicyRevisionsAsync(project.Id);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Null(listed[0].PolicyJson, "Listing revisions must not decrypt one policy blob per row.");
    }

    [Test]
    public async Task ActivatePolicy_SameHash_IsANoOpThatDoesNotResetTheCohort()
    {
        await using var context = await CreateDatabaseAsync("activate-noop.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        var first = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(first.Revision.Id, first.Revision.CohortGeneration, "key-a"));
        var afterFirst = AssertEx.NotNull(await store.GetProjectAsync(project.Id));

        var repeated = await store.ActivateJudgePolicyAsync(project.Id, afterFirst.Version, PolicyA, HashA);

        AssertEx.False(repeated.WasCreated, "Re-activating the current policy creates nothing.");
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id));
        AssertEx.Equal("key-a", current.ReferenceExecutionKey);
        AssertEx.Equal(expected: 1, current.CohortGeneration);
        AssertEx.Equal(afterFirst.Version, AssertEx.NotNull(await store.GetProjectAsync(project.Id)).Version);
    }

    [Test]
    public async Task ActivatePolicy_NewThenOldHash_ReusesTheOriginalRevisionAndResetsItsCohort()
    {
        await using var context = await CreateDatabaseAsync("activate-reuse.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        var first = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(first.Revision.Id, first.Revision.CohortGeneration, "key-a"));

        var second = await store.ActivateJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id), PolicyB, HashB);
        AssertEx.True(second.WasCreated);
        AssertEx.Equal(expected: 2, second.Revision.Revision);
        AssertEx.Null(second.Revision.ReferenceExecutionKey, "A new revision starts its own, open cohort.");

        var back = await store.ActivateJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id), PolicyA, HashA);

        AssertEx.False(back.WasCreated, "Returning to a policy the project has held before must reuse its revision.");
        AssertEx.Equal(first.Revision.Id, back.Revision.Id);
        AssertEx.Equal(expected: 1, back.Revision.Revision);
        AssertEx.Null(back.Revision.ReferenceExecutionKey, "Every activation resets the cohort, reuse included.");
        AssertEx.Equal(expected: 2, back.Revision.CohortGeneration);
        AssertEx.Equal(expected: 2, (await store.ListJudgePolicyRevisionsAsync(project.Id)).Count);
    }

    [Test]
    public async Task DisablePolicy_ClearsThePointerAndKeepsTheRevisionHistory()
    {
        await using var context = await CreateDatabaseAsync("disable.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        _ = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);

        await store.DisableJudgePolicyAsync(project.Id, await CurrentVersionAsync(store, project.Id));

        AssertEx.Null(await store.GetCurrentJudgePolicyRevisionAsync(project.Id), "Disabling clears the pointer.");
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(project.Id)).Count, "Revisions are history and stay.");
    }

    [Test]
    public async Task ActivateAndDisable_WhileAnAttemptIsActive_AreRefusedWithoutWritingAnything()
    {
        await using var context = await CreateDatabaseAsync("activate-blocked.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        _ = await SucceedRunAsync(store, project, revision);

        var version = await CurrentVersionAsync(store, project.Id);
        var queued = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.ActivateJudgePolicyAsync(project.Id, version, PolicyB, HashB));
        AssertEx.Equal("JudgeAttemptsActive", queued.Code);
        var disabled = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.DisableJudgePolicyAsync(project.Id, version));
        AssertEx.Equal("JudgeAttemptsActive", disabled.Code);

        // A refused activation must be atomic: no half-created revision two, no moved pointer, no version bump.
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(project.Id)).Count);
        AssertEx.Equal(revision.Id, AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id)).Id);
        AssertEx.Equal(version, await CurrentVersionAsync(store, project.Id));

        // Running, not just queued, is equally blocking.
        _ = AssertEx.NotNull(await store.ClaimNextAsync());
        var running = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.DisableJudgePolicyAsync(project.Id, version));
        AssertEx.Equal("JudgeAttemptsActive", running.Code);
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithACurrentPolicy_InsertsAttemptOneAndItsWorkItemAtomically()
    {
        await using var context = await CreateDatabaseAsync("attempt-one.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);

        var run = await SucceedRunAsync(store, project, revision);

        var attempts = await ReadAttemptsAsync(context, run.Id);
        AssertEx.Equal(expected: 1, attempts.Count);
        AssertEx.Equal(expected: 1, attempts[0].Sequence);
        AssertEx.Equal(revision.Id, attempts[0].PolicyRevisionId);
        AssertEx.Equal(revision.CohortGeneration, attempts[0].CohortGeneration);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Queued, attempts[0].Status);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkWorkKind.Judge, claimed.Kind);
        AssertEx.Equal(attempts[0].Id, RequireAttemptId(claimed));
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithoutACurrentPolicy_InsertsNoAttemptAndQueuesNoJudgeWork()
    {
        await using var context = await CreateDatabaseAsync("attempt-none.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());

        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version));

        AssertEx.Empty(await ReadAttemptsAsync(context, run.Id), "There is no policy to judge under.");
        _ = succeeded;
        AssertEx.Null(await store.ClaimNextAsync(), "No attempt means no claimable judge work.");
    }

    [Test]
    public async Task MarkPrimarySucceeded_WhenThePolicyMovedUnderTheRun_RollsBackAndThrows()
    {
        await using var context = await CreateDatabaseAsync("attempt-policy-changed.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store);
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());

        _ = await AssertEx.ThrowsAsync<BenchmarkJudgePolicyChangedException>(() =>
                              store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
                              {
                                  JudgeAttempt = new BenchmarkJudgeAttemptSeed(Guid.NewGuid(), new ReadOnlyMemory<byte>(JudgeRuntime))
                              }));

        // Rolled back with the transaction: primary success must not commit against a policy that is no longer current.
        var reloaded = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Running, reloaded.PrimaryStatus);
        AssertEx.Empty(await ReadAttemptsAsync(context, run.Id));
    }

    [Test]
    public async Task MarkPrimarySucceeded_WithoutAResolvedJudgeRuntime_InsertsAFailedAttemptAndATerminalWorkItem()
    {
        await using var context = await CreateDatabaseAsync("attempt-unresolved.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());

        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, RuntimeJson: null, "judge runtime unresolved")
        });

        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, succeeded.PrimaryStatus);
        var attempts = await ReadAttemptsAsync(context, run.Id);
        AssertEx.Equal(expected: 1, attempts.Count);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, attempts[0].Status);
        AssertEx.Equal("judge runtime unresolved", attempts[0].ErrorMessage);
        AssertEx.Null(attempts[0].JudgeRuntimeJson, "A pre-resolution failure has no runtime to store.");

        // The work item exists so the attempt/work-item invariant holds, but it is terminal: never claimable, and it
        // must not sit in front of the next run's primary work.
        var second = await store.StartRunAsync(NewRun(AssertEx.NotNull(await store.GetProjectAsync(project.Id))));
        var next = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(second.Id, next.RunId);
        AssertEx.Equal(BenchmarkWorkKind.Primary, next.Kind);
    }

    [Test]
    public async Task EnqueueJudgeAttempt_AppliesEveryRefusalRuleAndForceBypassesTheAlreadyAppliedGuard()
    {
        await using var context = await CreateDatabaseAsync("enqueue-rules.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunAsync(store, project, revision);

        // The run's first attempt is still queued.
        var active = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, run.Version, revision.Id)));
        AssertEx.Equal("JudgeAttemptActive", active.Code);

        // A revision that is not the project's current one is a retryable policy change, not a validation error.
        _ = await AssertEx.ThrowsAsync<BenchmarkJudgePolicyChangedException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, run.Version, Guid.NewGuid())));

        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}")));
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Succeeded, (await ReadAttemptsAsync(context, run.Id))[0].Status);

        // Not ranked yet: no execution key, so re-judging is allowed and inserts attempt two.
        var second = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, judged.Version, revision.Id));
        AssertEx.Equal(expected: 2, second.Sequence);

        var secondJudge = AssertEx.NotNull(await store.ClaimNextAsync());
        var secondJudged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, secondJudge.Version, Encoding.UTF8.GetBytes("{}")));

        // Launch readiness normally writes the execution key; this test sets it directly to isolate the
        // ranked-cohort guard.
        await SetExecutionKeyAsync(context, second.Id, "key-a");
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a"));

        var applied = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, secondJudged.Version, revision.Id)));
        AssertEx.Equal("JudgePolicyAlreadyApplied", applied.Code);

        var forced = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, secondJudged.Version, revision.Id) with
        {
            Force = true
        });
        AssertEx.Equal(expected: 3, forced.Sequence);
        AssertEx.Equal(expected: 3, (await ReadAttemptsAsync(context, run.Id)).Count, "Every judging is its own row; nothing is overwritten.");
    }

    [Test]
    public async Task EnqueueJudgeAttempt_WhenTheCohortGenerationMoved_IsAllowedWithoutForce()
    {
        await using var context = await CreateDatabaseAsync("enqueue-stale-generation.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunAsync(store, project, revision);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}")));
        await SetExecutionKeyAsync(context, RequireAttemptId(judge), "key-a");
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a"));

        // Back to the same policy: the revision is reused but the cohort generation moves, so the ranked attempt is
        // stale and a plain re-judge must go through without force.
        var reactivated = await store.ActivateJudgePolicyAsync(project.Id,
            await CurrentVersionAsync(store, project.Id),
            PolicyB,
            HashB);
        var backToA = await store.ActivateJudgePolicyAsync(project.Id,
            await CurrentVersionAsync(store, project.Id),
            PolicyA,
            HashA);
        AssertEx.Equal(revision.Id, backToA.Revision.Id);
        AssertEx.Equal(expected: 2, backToA.Revision.CohortGeneration);
        AssertEx.Equal(expected: 2, reactivated.Revision.Revision);

        var next = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, judged.Version, revision.Id));

        AssertEx.Equal(expected: 2, next.Sequence);
        AssertEx.Equal(expected: 2, next.CohortGeneration, "A new attempt is stamped with the live generation.");
    }

    [Test]
    public async Task SetUserScore_AcceptsTheWholeRangeAndClearsWithNull()
    {
        await using var context = await CreateDatabaseAsync("user-score.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version));

        var zero = await store.SetUserScoreAsync(run.Id, score: 0, succeeded.Version);
        AssertEx.Equal(expected: 0, zero.UserScore);
        var hundred = await store.SetUserScoreAsync(run.Id, score: 100, zero.Version);
        AssertEx.Equal(expected: 100, hundred.UserScore);
        var cleared = await store.SetUserScoreAsync(run.Id, score: null, hundred.Version);
        AssertEx.Null(cleared.UserScore, "Null clears the operator override.");

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.SetUserScoreAsync(run.Id, score: 101, cleared.Version));
    }

    [Test]
    public async Task DeleteRun_RemovesWorkAttemptsAndTheRunAndResetsTheCohortWhenItWasTheLast()
    {
        var databasePath = GetDatabasePath("delete-order.sqlite");
        Guid projectId;
        await using (var context = await CreateDatabaseAsync(databasePath, create: true))
        {
            var store = new BenchmarkStore(context, TimeProvider.System);
            var (project, revision) = await CreateJudgeProjectAsync(store);
            projectId = project.Id;
            var run = await SucceedRunAsync(store, project, revision);
            var judge = AssertEx.NotNull(await store.ClaimNextAsync());
            var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}")));
            await SetExecutionKeyAsync(context, RequireAttemptId(judge), "key-a");
            AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a"));

            await store.DeleteRunAsync(run.Id, judged.Version);

            // Deleting the project's last run leaves a cohort nothing is measured against; it reopens.
            var afterDelete = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(projectId));
            AssertEx.Null(afterDelete.ReferenceExecutionKey);
            AssertEx.Equal(expected: 2, afterDelete.CohortGeneration);

            await store.DeleteProjectAsync(projectId, await CurrentVersionAsync(store, projectId));
        }

        // Foreign keys off is the real node configuration, so nothing catches an orphan for us.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            _ = await pragma.ExecuteNonQueryAsync();
        }

        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_work_items;"));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_judge_attempts;"));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_runs;"));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_judge_policy_revisions;"));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM benchmark_projects;"));
    }

    [Test]
    public async Task RecoverOnStartup_MarksRunningAttemptsFailedWithoutTouchingResults()
    {
        await using var context = await CreateDatabaseAsync("recover-attempts.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunAsync(store, project, revision);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Running, (await ReadAttemptsAsync(context, run.Id))[0].Status);

        _ = await store.RecoverRunsOnStartupAsync();

        var attempts = await ReadAttemptsAsync(context, run.Id);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, attempts[0].Status);
        AssertEx.Equal("Interrupted by application restart.", attempts[0].ErrorMessage);
        AssertEx.Null(attempts[0].ResultJson, "Recovery never invents a result.");
        _ = judge;
    }

    [Test]
    public async Task JudgeSuccess_PromotesTheCohortOnTheFirstSuccess_NotAtReadiness()
    {
        await using var context = await CreateDatabaseAsync("promote-on-success.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunAsync(store, project, revision);

        // The first attempt reaches readiness under runtime A and then fails. A failed judging must not define the
        // cohort, or one bad launch would exclude every later run that ran correctly on a different runtime.
        var first = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeLaunchReadyAsync(RequireAttemptId(first), first.QueueSequence, first.Version, Receipt(), "runtime-a");
        var failed = await store.MarkJudgeFailedAsync(run.Id, first.Version, "judge blew up");
        AssertEx.Null(AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id)).ReferenceExecutionKey,
            "A failed first attempt must leave the cohort open.");

        var second = await store.EnqueueJudgeAttemptAsync(Enqueue(run.Id, failed.Version, revision.Id));
        var secondWork = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeLaunchReadyAsync(second.Id, secondWork.QueueSequence, secondWork.Version, Receipt(), "runtime-b");
        _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, secondWork.Version, Encoding.UTF8.GetBytes("{}"), 5, 73));

        AssertEx.Equal("runtime-b", AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id)).ReferenceExecutionKey,
            "The first SUCCESS of the live generation defines the cohort.");
        var persisted = AssertEx.NotNull(await store.GetJudgeAttemptAsync(second.Id));
        AssertEx.Equal<int?>(73, persisted.Score);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Succeeded, persisted.Status);
    }

    [Test]
    public async Task RunJudgeView_ReportsWhyARunIsNotRanked()
    {
        await using var context = await CreateDatabaseAsync("judge-view.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunAsync(store, project, revision);

        var queued = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Queued, queued.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgePending, queued.RankExclusionReason);
        AssertEx.True(queued.PolicyCurrent, "The attempt was enqueued under the project's current revision.");
        AssertEx.False(queued.ExecutionCurrent, "Nothing has claimed the cohort yet.");

        var work = AssertEx.NotNull(await store.ClaimNextAsync());
        var judged = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, work.Version, Encoding.UTF8.GetBytes("{}"), 5, 61));

        // Succeeded and scored, but the launch never produced an execution key, so it can never be ranked.
        var incomplete = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Succeeded, incomplete.State);
        AssertEx.Equal<int?>(61, incomplete.Score);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonExecutionIdentityIncomplete, incomplete.RankExclusionReason);

        // An operator score always ranks, whatever the judge did.
        var scored = await store.SetUserScoreAsync(run.Id, score: 88, judged.Version);
        var ranked = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id)).Judge);
        AssertEx.Null(ranked.RankExclusionReason, "An operator override is always part of the ranking.");
        AssertEx.Equal<int?>(88, scored.UserScore);

        // A run with no attempt at all is `none`, and unscored.
        var plainProject = await store.CreateProjectAsync(NewProject());
        var plainRun = await store.StartRunAsync(NewRun(plainProject));
        var none = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(plainRun.Id)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, none.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonNoScore, none.RankExclusionReason);
    }

    // An operator who cancels a judging did not watch it fail. Folding the two into `judge-failed` told the operator to
    // go looking for a broken judge when all the run needs is a re-judge.
    [Test]
    public async Task RunJudgeView_ReportsACancelledJudgingSeparatelyFromAFailedOne()
    {
        await using var context = await CreateDatabaseAsync("judge-view-cancelled.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);

        var cancelledRun = await SucceedRunAsync(store, project, revision);
        var cancelledWork = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeCancelledAsync(cancelledRun.Id, cancelledWork.Version);

        // Starting a run bumps the project's version, and StartRunAsync is version-checked against it.
        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
        var failedRun = await SucceedRunAsync(store, current, revision);
        var failedWork = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeFailedAsync(failedRun.Id, failedWork.Version, "judge blew up");

        var cancelled = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(cancelledRun.Id)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Cancelled, cancelled.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgeCancelled, cancelled.RankExclusionReason);

        var failed = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(failedRun.Id)).Judge);
        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, failed.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgeFailed, failed.RankExclusionReason);
    }

    // A verifier that could not RUN is a third fact again: the run is unranked, but the fix is an operator action on
    // the node (enable Compute, install bubblewrap) rather than a re-judge, and it is emphatically not a score of 0 --
    // 0 is something an answer earns, and "unmeasurable" is not.
    [Test]
    public async Task RunJudgeView_ReportsAnUnusableVerifierSeparatelyFromAFailedJudging()
    {
        await using var context = await CreateDatabaseAsync("judge-view-verifier-unavailable.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);

        var run = await SucceedRunAsync(store, project, revision);
        var work = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeFailedAsync(run.Id,
                           work.Version,
                           BenchmarkRunJudgeStates.VerifierUnavailablePrefix
                           + "The Python compute tool is disabled on this node (Compute:Enabled=false).");

        var judge = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id)).Judge);

        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, judge.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonVerifierUnavailable, judge.RankExclusionReason);
    }

    // And a judging refused because the item's verifier override named a criterion the rubric does not have is a
    // fourth fact: the run is unranked, and the fix is an edit to the item or the rubric -- not a re-judge, not an
    // operator action on the node, and emphatically not a score, which is what grading under the policy's own
    // configuration would have produced.
    [Test]
    public async Task RunJudgeView_ReportsAnUnmatchedItemOverrideSeparatelyFromAFailedJudging()
    {
        await using var context = await CreateDatabaseAsync("judge-view-override-unmatched.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);

        var run = await SucceedRunAsync(store, project, revision);
        var work = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeFailedAsync(run.Id,
                           work.Version,
                           BenchmarkRunJudgeStates.OverrideUnmatchedPrefix
                           + "The task item's verifier override names criterion 'needle', which the judge rubric does not have.");

        var judge = AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(run.Id)).Judge);

        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, judge.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonOverrideUnmatched, judge.RankExclusionReason);
    }

    [Test]
    public async Task ActivatePolicy_WithACohortSeed_EnqueuesEveryEligibleRunInTheNewGeneration()
    {
        await using var context = await CreateDatabaseAsync("activate-cohort.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store);
        var eligible = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            eligible.Add((await SucceedRunWithoutJudgeRuntimeAsync(store, project.Id)).Id);
        }

        var ineligible = await FailRunAsync(store, project.Id);

        var activation = await store.ActivateJudgePolicyAsync(project.Id,
                                        await CurrentVersionAsync(store, project.Id),
                                        PolicyB,
                                        HashB,
                                        Seed());

        AssertEx.Equal(expected: 3, activation.SucceededRunIds.Count, "A cohort reset covers the complete eligible set, never a subset.");
        foreach (var runId in eligible)
        {
            var attempts = await ReadAttemptsAsync(context, runId);
            AssertEx.Equal(expected: 2, attempts.Count, "The eligible run keeps its history and gains exactly one attempt.");
            AssertEx.Equal(BenchmarkJudgeAttemptStatus.Queued, attempts[^1].Status);
            AssertEx.Equal(activation.Revision.Id, attempts[^1].PolicyRevisionId);
            AssertEx.Equal(activation.Revision.CohortGeneration, attempts[^1].CohortGeneration, "Every attempt of the reset belongs to one generation.");
            AssertEx.Equal(attempts[^1].Id, AssertEx.NotNull(await store.GetRunAsync(runId)).Judge?.AttemptId);
        }

        AssertEx.Empty(await ReadAttemptsAsync(context, ineligible.Id),
            "A run without stored output can never be judged, so it stays out of the cohort.");
        context.ChangeTracker.Clear();
        AssertEx.Equal(expected: 3,
            await context.BenchmarkWorkItems.AsNoTracking()
                         .CountAsync(entity => entity.Kind == BenchmarkWorkKind.Judge && entity.Status == BenchmarkWorkStatus.Queued),
            "Each enqueued attempt carries its own queued work item.");
    }

    [Test]
    public async Task ActivatePolicy_WithACohortSeed_WhenTheCommitFails_RollsBackTheResetToo()
    {
        var interceptor = new FailingSaveInterceptor();
        await using var context = await CreateDatabaseAsync("activate-cohort-rollback.sqlite", interceptor);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunWithoutJudgeRuntimeAsync(store, project.Id);
        var version = await CurrentVersionAsync(store, project.Id);

        // Fails the save that stores the attempts, after the staged save that created the new revision: the whole
        // activation is one transaction, so a partially enqueued cohort must not survive it.
        interceptor.FailAfter(saves: 1);
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            store.ActivateJudgePolicyAsync(project.Id, version, PolicyB, HashB, Seed()));

        context.ChangeTracker.Clear();
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(project.Id)).Count, "The new revision rolled back with the attempts.");
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id));
        AssertEx.Equal(revision.Id, current.Id, "The pointer never moved.");
        AssertEx.Equal(revision.CohortGeneration, current.CohortGeneration, "The cohort was not reset.");
        AssertEx.Equal(version, await CurrentVersionAsync(store, project.Id));
        AssertEx.Equal(expected: 1, (await ReadAttemptsAsync(context, run.Id)).Count, "Attempts are all-or-nothing with the reset.");
    }

    [Test]
    public async Task BeginProjectRejudge_WithACohortSeed_EnqueuesTheSetAndRefusesAStaleRevision()
    {
        await using var context = await CreateDatabaseAsync("rejudge-cohort.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await SucceedRunWithoutJudgeRuntimeAsync(store, project.Id);

        var version = await CurrentVersionAsync(store, project.Id);
        var stale = await AssertEx.ThrowsAsync<BenchmarkJudgePolicyChangedException>(() =>
                                      store.BeginProjectRejudgeAsync(project.Id,
                                          version,
                                          new BenchmarkJudgeAttemptSeed(Guid.NewGuid(), new ReadOnlyMemory<byte>(JudgeRuntime))));
        AssertEx.True(stale.Message.Length > 0, "The refusal must say why.");
        AssertEx.Equal(version, await CurrentVersionAsync(store, project.Id), "A refused re-judge writes nothing.");
        AssertEx.Equal(expected: 1, (await ReadAttemptsAsync(context, run.Id)).Count, "A stale runtime enqueues nothing.");

        var rejudge = await store.BeginProjectRejudgeAsync(project.Id,
                                     await CurrentVersionAsync(store, project.Id),
                                     new BenchmarkJudgeAttemptSeed(revision.Id, new ReadOnlyMemory<byte>(JudgeRuntime)));

        AssertEx.Equal(expected: 1, rejudge.SucceededRunIds.Count);
        AssertEx.Equal(revision.CohortGeneration + 1, rejudge.Revision.CohortGeneration);
        var attempts = await ReadAttemptsAsync(context, run.Id);
        AssertEx.Equal(expected: 2, attempts.Count);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Queued, attempts[^1].Status);
        AssertEx.Equal(rejudge.Revision.CohortGeneration, attempts[^1].CohortGeneration);
    }

    [Test]
    public async Task CreateProject_WithAJudgePolicy_ActivatesItInTheSameTransaction()
    {
        await using var context = await CreateDatabaseAsync("create-with-judge.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);

        var project = await store.CreateProjectAsync(NewProject(), new BenchmarkJudgePolicyChangeInput(PolicyA, HashA));

        AssertEx.True(project.JudgeEnabled, "A project created with a judge is never persisted with judging off.");
        var current = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id));
        AssertEx.Equal(HashA, current.PolicyHash);
        AssertEx.Equal(project.CurrentJudgePolicyRevisionId, current.Id);
        AssertEx.Equal(expected: 1, project.Version, "Creation is one write, so it is version one.");
    }

    [Test]
    public async Task CreateProject_WhenTheJudgePolicyIsRejected_PersistsNoProjectAtAll()
    {
        await using var context = await CreateDatabaseAsync("create-with-judge-rollback.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var input = NewProject();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.CreateProjectAsync(input, new BenchmarkJudgePolicyChangeInput(PolicyA, "not-a-policy-hash")));

        context.ChangeTracker.Clear();
        AssertEx.Null(await store.GetProjectAsync(input.Id), "A retry must not have to work around a half-created project.");
        AssertEx.Empty(await store.ListJudgePolicyRevisionsAsync(input.Id));
    }

    [Test]
    public async Task UpdateProject_WithAJudgeChange_AppliesBothHalvesOrNeither()
    {
        await using var context = await CreateDatabaseAsync("update-with-judge.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var created = await store.CreateProjectAsync(NewProject(), new BenchmarkJudgePolicyChangeInput(PolicyA, HashA));

        var renamed = await store.UpdateProjectAsync(created.Id,
                                     created.Version,
                                     NewProject(created.Id) with
                                     {
                                         Name = "Renamed"
                                     },
                                     new BenchmarkJudgePolicyChangeInput(PolicyB, HashB));

        AssertEx.Equal("Renamed", renamed.Name);
        AssertEx.Equal(HashB, AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(created.Id)).PolicyHash);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.UpdateProjectAsync(created.Id,
                renamed.Version,
                NewProject(created.Id) with
                {
                    Name = "Rolled back"
                },
                new BenchmarkJudgePolicyChangeInput(PolicyA, "not-a-policy-hash")));

        context.ChangeTracker.Clear();
        var unchanged = AssertEx.NotNull(await store.GetProjectAsync(created.Id));
        AssertEx.Equal("Renamed", unchanged.Name, "A rejected judge change takes the field edit down with it.");
        AssertEx.Equal(renamed.Version, unchanged.Version);
        AssertEx.Equal(HashB, AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(created.Id)).PolicyHash);
    }

    [Test]
    public async Task UpdateProject_WithADisablingChange_TurnsJudgingOffWithTheEdit()
    {
        await using var context = await CreateDatabaseAsync("update-disables-judge.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var created = await store.CreateProjectAsync(NewProject(), new BenchmarkJudgePolicyChangeInput(PolicyA, HashA));

        var updated = await store.UpdateProjectAsync(created.Id, created.Version, NewProject(created.Id), BenchmarkJudgePolicyChangeInput.Disabled);

        AssertEx.False(updated.JudgeEnabled);
        AssertEx.Null(await store.GetCurrentJudgePolicyRevisionAsync(created.Id));
        AssertEx.Equal(expected: 1, (await store.ListJudgePolicyRevisionsAsync(created.Id)).Count, "Revisions are history and stay.");
    }

    private static BenchmarkJudgeAttemptSeed Seed() =>
        new(null, new ReadOnlyMemory<byte>(JudgeRuntime));

    /// <summary>
    ///     A succeeded run whose automatic judging failed for want of a runtime: eligible for a cohort reset, and
    ///     terminal, so the reset is not refused for an active attempt.
    /// </summary>
    private static async Task<BenchmarkRunRecord> SucceedRunWithoutJudgeRuntimeAsync(BenchmarkStore store, Guid projectId)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version));
    }

    private static async Task<BenchmarkRunRecord> FailRunAsync(BenchmarkStore store, Guid projectId)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkPrimaryFailedAsync(run.Id, primary.Run.Version, "boom");
    }

    /// <summary>Fails one save inside the store's transaction, so a test can assert the rollback covers everything.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        private int _skip = -1;

        public void FailAfter(int saves) =>
            _skip = saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_skip < 0)
            {
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            if (_skip-- == 0)
            {
                _skip = -1;
                throw new InvalidOperationException("Injected save failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new("{}", "{}", new string('e', count: 64), new string('r', count: 64), "identity", "cpu", null, null,
            new string('x', count: 64), false, "auto");

    [Test]
    public async Task TryPromoteReferenceExecutionKey_PromotesOnceAndRefusesStaleGenerations()
    {
        await using var context = await CreateDatabaseAsync("promote-cas.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject());
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);
        var revision = activation.Revision;

        AssertEx.False(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration + 1, "key-a"),
            "An attempt stamped with another generation must never define the cohort.");
        AssertEx.True(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-a"));
        AssertEx.False(await store.TryPromoteReferenceExecutionKeyAsync(revision.Id, revision.CohortGeneration, "key-b"),
            "The reference key is insert-if-null; the first same-generation success owns it.");
        AssertEx.Equal("key-a", AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id)).ReferenceExecutionKey);
    }

    private static Guid RequireAttemptId(BenchmarkClaimedWork work)
    {
        AssertEx.True(work.JudgeAttemptId is not null, "Claimed judge work must name the attempt it judges.");
        return work.JudgeAttemptId!.Value;
    }

    private static async Task<long> CurrentVersionAsync(BenchmarkStore store, Guid projectId) =>
        AssertEx.NotNull(await store.GetProjectAsync(projectId)).Version;

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite, no interpolation.
        command.CommandText = sql;
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<BenchmarkJudgeAttempt>> ReadAttemptsAsync(NodeChatDbContext context, Guid runId)
    {
        context.ChangeTracker.Clear();
        return await context.BenchmarkJudgeAttempts.AsNoTracking()
                            .Where(entity => entity.RunId == runId)
                            .OrderBy(entity => entity.Sequence)
                            .ToArrayAsync();
    }

    private static async Task SetExecutionKeyAsync(NodeChatDbContext context, Guid attemptId, string executionKey)
    {
        context.ChangeTracker.Clear();
        _ = await context.BenchmarkJudgeAttempts.Where(entity => entity.Id == attemptId)
                         .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.JudgeExecutionKey, executionKey));
        context.ChangeTracker.Clear();
    }

    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision)> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(NewProject());
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id)), activation.Revision);
    }

    private static async Task<BenchmarkRunRecord> SucceedRunAsync(BenchmarkStore store,
        BenchmarkProjectRecord project,
        BenchmarkJudgePolicyRevisionRecord revision)
    {
        var run = await store.StartRunAsync(NewRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, new ReadOnlyMemory<byte>(JudgeRuntime))
        });
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
        await CreateDatabaseAsync(GetDatabasePath(fileName), create: true);

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName, IInterceptor extraInterceptor)
    {
        var context = AgentDefinitionTestContextFactory.Create(GetDatabasePath(fileName), _keyHolder, extraInterceptor);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string databasePath, bool create)
    {
        var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        if (create)
        {
            _ = await context.Database.EnsureDeletedAsync();
            _ = await context.Database.EnsureCreatedAsync();
        }

        return context;
    }

    private string GetDatabasePath(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
