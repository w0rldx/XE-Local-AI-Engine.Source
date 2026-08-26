namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The queue's claim and its restart recovery both used to branch "Primary, else it is a Judge" — and the else
///     dereferenced the judge attempt id. A Fidelity or Comparison item reaching either would throw
///     <c>InvalidJudgeTransition</c> and stall the single-consumer queue behind an item it could never claim. Both are
///     now four-arm switches, and this covers every cell of that table.
/// </summary>
public sealed class BenchmarkWorkKindLifecycleTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] PolicyBytes = Encoding.UTF8.GetBytes("{\"rubric\":\"v1\"}");
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ClaimNextAsync_DispatchesFidelityAndComparisonInFifoOrder()
    {
        await using var context = await CreateSchemaAsync("claim-four-kinds.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);

        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Primary, primary.Kind);

        var fidelityAttemptId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var comparisonId = await InsertComparisonWorkAsync(context, project.Id, revision.Id, run.Id).ConfigureAwait(false);

        var fidelity = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Fidelity, fidelity.Kind);
        AssertEx.Equal<Guid?>(fidelityAttemptId, fidelity.FidelityAttemptId);
        AssertEx.Null(fidelity.ComparisonId, "A fidelity claim must not carry a comparison id.");
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Running,
            (await context.BenchmarkFidelityAttempts.AsNoTracking().SingleAsync(entity => entity.Id == fidelityAttemptId).ConfigureAwait(false)).Status,
            "Claiming a fidelity item moves its attempt to Running.");

        var comparison = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Comparison, comparison.Kind);
        AssertEx.Equal<Guid?>(comparisonId, comparison.ComparisonId);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Running,
            (await context.BenchmarkComparisons.AsNoTracking().SingleAsync(entity => entity.Id == comparisonId).ConfigureAwait(false)).Status,
            "Claiming a comparison item moves the comparison to Running.");
    }

    [Test]
    public async Task ClaimNextAsync_FidelityItemNamingNoAttempt_ThrowsNotFound()
    {
        await using var context = await CreateSchemaAsync("claim-fidelity-orphan.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        // The CHECK forbids a NULL attempt id, so the reachable version of "orphan" is an id naming no row.
        context.BenchmarkWorkItems.Add(new BenchmarkWorkItem
        {
            RunId = run.Id,
            Kind = BenchmarkWorkKind.Fidelity,
            FidelityAttemptId = Guid.NewGuid(),
            Status = BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = 1
        });
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Its own exception code, not the judge's: an operator reading the log has to be able to tell which arm failed.
        _ = await AssertEx.ThrowsAsync<BenchmarkNotFoundException>(() => store.ClaimNextAsync()).ConfigureAwait(false);
    }

    [Test]
    public async Task ClaimNextAsync_FidelityAttemptAlreadyTerminal_ThrowsInvalidFidelityTransition()
    {
        await using var context = await CreateSchemaAsync("claim-fidelity-terminal.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var attemptId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);

        var attempt = await context.BenchmarkFidelityAttempts.SingleAsync(entity => entity.Id == attemptId).ConfigureAwait(false);
        attempt.Status = BenchmarkJudgeAttemptStatus.Cancelled;
        await context.SaveChangesAsync().ConfigureAwait(false);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.ClaimNextAsync()).ConfigureAwait(false);
        AssertEx.Equal("InvalidFidelityTransition", conflict.Code);
    }

    [Test]
    public async Task ClaimNextAsync_ComparisonAlreadyTerminal_ThrowsInvalidComparisonTransition()
    {
        await using var context = await CreateSchemaAsync("claim-comparison-terminal.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var comparisonId = await InsertComparisonWorkAsync(context, project.Id, revision.Id, run.Id, BenchmarkJudgeAttemptStatus.Failed).ConfigureAwait(false);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.ClaimNextAsync()).ConfigureAwait(false);
        AssertEx.Equal("InvalidComparisonTransition", conflict.Code);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed,
            (await context.BenchmarkComparisons.AsNoTracking().SingleAsync(entity => entity.Id == comparisonId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The fidelity projection follows its attempt through every transition. Claiming used to update only the
    ///     attempt, so a measurement that runs for hours kept reading 'queued' on the run every API projects.
    /// </summary>
    [Test]
    public async Task ClaimNextAsync_Fidelity_ProjectsRunningOnTheRunAndTerminalizesBack()
    {
        await using var context = await CreateSchemaAsync("claim-fidelity-running.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var attemptId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);

        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Fidelity, claimed.Kind);
        context.ChangeTracker.Clear();
        AssertEx.Equal("running",
            (await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false)).FidelityStatus);

        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, claimed.Version, attemptId, PerplexityMean: 6.7983))
                       .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        AssertEx.Equal("succeeded",
            (await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false)).FidelityStatus,
            "Terminalization still returns the projection to a terminal state.");
    }

    /// <summary>
    ///     A comparison is not an event in either run's life. Claiming one used to fall through to the common run
    ///     version bump on the canonical first run, which invalidated that run's CAS token on every pairwise claim:
    ///     scoring, deleting or re-measuring it returned VersionConflict throughout a tournament.
    /// </summary>
    [Test]
    public async Task ClaimNextAsync_Comparison_LeavesBothRunsVersionsAlone()
    {
        await using var context = await CreateSchemaAsync("claim-comparison-versions.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var first = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        project = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var second = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        var before = await context.BenchmarkRuns.AsNoTracking().OrderBy(entity => entity.CreatedAtUtc).ToListAsync().ConfigureAwait(false);
        var canonical = string.CompareOrdinal(first.Id.ToString(), second.Id.ToString()) < 0
            ? (Left: first.Id, Right: second.Id)
            : (Left: second.Id, Right: first.Id);
        _ = await InsertComparisonWorkAsync(context, project.Id, revision.Id, canonical.Left, pair: canonical).ConfigureAwait(false);

        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Comparison, claimed.Kind);

        context.ChangeTracker.Clear();
        var after = await context.BenchmarkRuns.AsNoTracking().OrderBy(entity => entity.CreatedAtUtc).ToListAsync().ConfigureAwait(false);
        AssertEx.Equal(before[0].Version, after[0].Version, "The canonical run of the pair keeps the CAS token its caller is holding.");
        AssertEx.Equal(before[1].Version, after[1].Version, "The other run of the pair is not touched either.");
    }

    [Test]
    public async Task Recovery_RunningFidelityAttempt_FailsTheRunsFidelityStatusAndKeepsTheNumbers()
    {
        await using var context = await CreateSchemaAsync("recover-fidelity.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        // A first measurement that succeeded, then a second one killed mid-flight.
        var first = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var firstClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, firstClaim.Version, first,
                PerplexityMean: 6.7983, PerplexityStdErr: 0.07405, PerplexityChunks: 200, PerplexityContextTokens: 512, CorpusId: "wikitext2-raw-test@abc"))
            .ConfigureAwait(false);
        var second = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        _ = await store.RecoverRunsOnStartupAsync().ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var interrupted = await context.BenchmarkFidelityAttempts.AsNoTracking().SingleAsync(entity => entity.Id == second).ConfigureAwait(false);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, interrupted.Status, "A fidelity attempt whose process died must not stay Running forever.");

        var recovered = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false);
        AssertEx.Equal<Guid?>(first, recovered.FidelityAttemptId, "The projection keeps pointing at the attempt that actually succeeded.");
        AssertEx.Equal<double?>(6.7983, recovered.PerplexityMean, "An interrupted re-measurement must not erase the previous number.");

        // The status is the half that MUST move. Left reading 'queued' with no attempt and no work item behind it,
        // every API reports an active measurement forever and the UI keeps re-measure disabled.
        AssertEx.Equal("failed", recovered.FidelityStatus, "A measurement whose process died is not still queued.");
        AssertEx.True(recovered.FidelityErrorMessage is { Length: > 0 }, "The run carries the reason its measurement stopped.");
        AssertEx.Empty(await context.BenchmarkWorkItems.AsNoTracking()
                                    .Where(item => item.Kind == BenchmarkWorkKind.Fidelity
                                                   && (item.Status == BenchmarkWorkStatus.Queued || item.Status == BenchmarkWorkStatus.Running))
                                    .ToListAsync()
                                    .ConfigureAwait(false));
    }

    [Test]
    public async Task DeleteRunAsync_RemovesTheRunsFidelityAttempts()
    {
        await using var context = await CreateSchemaAsync("delete-fidelity-attempts.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id, primary.Run.Version,
                Encoding.UTF8.GetBytes("[{\"text\":\"answer\"}]"), 1, 4096, 100, 12, 120))
            .ConfigureAwait(false);
        var attemptId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, claimed.Version, attemptId, PerplexityMean: 6.7983))
                       .ConfigureAwait(false);

        await store.DeleteRunAsync(run.Id, AssertEx.NotNull(await store.GetRunAsync(run.Id).ConfigureAwait(false)).Version).ConfigureAwait(false);

        // Foreign keys are off, so an attempt row nothing deletes simply survives its run forever — and it carries an
        // encrypted receipt, which makes it a leak rather than a tidiness problem.
        context.ChangeTracker.Clear();
        AssertEx.Empty(await context.BenchmarkFidelityAttempts.AsNoTracking().Where(entity => entity.RunId == run.Id).ToListAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task RequeueFidelityAsync_PutsTheClaimedItemBackInTheQueueRatherThanFailingIt()
    {
        await using var context = await CreateSchemaAsync("requeue-fidelity.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var attemptId = await store.EnqueueFidelityAsync(run.Id, "kld").ConfigureAwait(false);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        var requeued = await store.RequeueFidelityAsync(run.Id, claimed.Version, "another process holds the base logits").ConfigureAwait(false);

        // Still queued, with the reason beside it: "waiting on another process" and "this will not happen" must not
        // read the same, and a fidelity work item pins attempt = 1, so a failure here would have no retry behind it.
        var projection = AssertEx.NotNull(requeued.Fidelity);
        AssertEx.Equal("queued", projection.Status);
        AssertEx.True(AssertEx.NotNull(projection.ErrorMessage).Contains("another process", StringComparison.Ordinal),
            "The reason travels with the item so a reader can tell waiting from failed.");
        context.ChangeTracker.Clear();
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Queued,
            (await context.BenchmarkFidelityAttempts.AsNoTracking().SingleAsync(entity => entity.Id == attemptId).ConfigureAwait(false)).Status);

        var reclaimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Fidelity, reclaimed.Kind);
        AssertEx.Equal<Guid?>(attemptId, reclaimed.FidelityAttemptId, "The consumer picks the same measurement up again on its next claim.");
    }

    [Test]
    public async Task RequeueFidelityAsync_OnAnAlreadyTerminalAttempt_ChangesNothing()
    {
        await using var context = await CreateSchemaAsync("requeue-fidelity-terminal.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var attemptId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, claimed.Version, attemptId, PerplexityMean: 6.7983))
                       .ConfigureAwait(false);

        // A requeue racing a completion must not start a second measurement of a cell that already has its number.
        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.RequeueFidelityAsync(run.Id, claimed.Version, "too late")).ConfigureAwait(false);
        AssertEx.Null(await store.ClaimNextAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task Recovery_RunningComparison_TerminalizesFailed()
    {
        await using var context = await CreateSchemaAsync("recover-comparison.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var comparisonId = await InsertComparisonWorkAsync(context, project.Id, revision.Id, run.Id).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        _ = await store.RecoverRunsOnStartupAsync().ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var comparison = await context.BenchmarkComparisons.AsNoTracking().SingleAsync(entity => entity.Id == comparisonId).ConfigureAwait(false);
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed, comparison.Status);

        // Terminal-failed, so the filtered live-slot index now permits the reconciler to re-enqueue this slot.
        var work = await context.BenchmarkWorkItems.AsNoTracking().SingleAsync(entity => entity.ComparisonId == comparisonId).ConfigureAwait(false);
        AssertEx.Equal(BenchmarkWorkStatus.Failed, work.Status);
    }

    /// <summary>
    ///     The pre-sweeps exist for the row a previous PARTIAL recovery already orphaned: its work item is terminal, so
    ///     the work-item pass never reaches it, and without a sweep of its own it stays Running for the life of the
    ///     database.
    /// </summary>
    [Test]
    public async Task Recovery_OrphanRunningAttemptWithTerminalWorkItem_IsSwept()
    {
        await using var context = await CreateSchemaAsync("recover-orphans.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var fidelityId = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var comparisonId = await InsertComparisonWorkAsync(context, project.Id, revision.Id, run.Id).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var fidelity = await context.BenchmarkFidelityAttempts.SingleAsync(entity => entity.Id == fidelityId).ConfigureAwait(false);
        fidelity.Status = BenchmarkJudgeAttemptStatus.Running;
        var comparison = await context.BenchmarkComparisons.SingleAsync(entity => entity.Id == comparisonId).ConfigureAwait(false);
        comparison.Status = BenchmarkJudgeAttemptStatus.Running;
        foreach (var work in await context.BenchmarkWorkItems.Where(entity => entity.Kind != BenchmarkWorkKind.Primary).ToListAsync().ConfigureAwait(false))
        {
            work.Status = BenchmarkWorkStatus.Failed;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);

        _ = await store.RecoverRunsOnStartupAsync().ConfigureAwait(false);

        context.ChangeTracker.Clear();
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed,
            (await context.BenchmarkFidelityAttempts.AsNoTracking().SingleAsync(entity => entity.Id == fidelityId).ConfigureAwait(false)).Status,
            "The fidelity pre-sweep must reach an attempt whose work item is already terminal.");
        AssertEx.Equal(BenchmarkJudgeAttemptStatus.Failed,
            (await context.BenchmarkComparisons.AsNoTracking().SingleAsync(entity => entity.Id == comparisonId).ConfigureAwait(false)).Status,
            "The comparison pre-sweep must do the same.");
    }

    /// <summary>
    ///     The projection is a copy of the LATEST succeeded attempt, and the guard is the attempt's SEQUENCE rather
    ///     than which terminalization happened to arrive last. The queue is single-consumer today, so the out-of-order
    ///     case is not reachable through it — which is exactly why the guard belongs on the write instead of being
    ///     inferred from the consumer's shape, and why this test seeds the newer row directly.
    /// </summary>
    [Test]
    public async Task FidelityProjection_IsOnlyRefreshedFromTheHighestSucceededAttempt()
    {
        await using var context = await CreateSchemaAsync("fidelity-projection.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        var first = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var firstClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        // A NEWER measurement lands and succeeds while the older one is still running.
        context.ChangeTracker.Clear();
        context.BenchmarkFidelityAttempts.Add(new BenchmarkFidelityAttempt
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Sequence = 9,
            Kind = "ppl",
            Status = BenchmarkJudgeAttemptStatus.Succeeded,
            PerplexityMean = 6.9513,
            EnqueuedAtUtc = 1,
            Version = 1
        });
        await context.SaveChangesAsync().ConfigureAwait(false);

        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, firstClaim.Version, first, PerplexityMean: 99.0))
            .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var afterStaleSuccess = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false);
        AssertEx.Null(afterStaleSuccess.PerplexityMean, "An attempt below the highest succeeded sequence must not become the projection.");
        AssertEx.Equal<Guid?>(null, afterStaleSuccess.FidelityAttemptId);
        AssertEx.Equal("succeeded", afterStaleSuccess.FidelityStatus, "The attempt itself still succeeded — only the projection refused it.");

        // And the ordinary path still projects: a fresh attempt above every succeeded sequence wins.
        var latest = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var latestClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkFidelitySucceededAsync(new BenchmarkFidelitySuccessCommand(run.Id, latestClaim.Version, latest,
                PerplexityMean: 6.7983, PerplexityStdErr: 0.07405, PerplexityChunks: 200, PerplexityContextTokens: 512, CorpusId: "wikitext2-raw-test@abc"))
            .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var projected = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false);
        AssertEx.Equal<Guid?>(latest, projected.FidelityAttemptId);
        AssertEx.Equal<double?>(6.7983, projected.PerplexityMean);
        AssertEx.Equal<double?>(0.07405, projected.PerplexityStdErr);
        AssertEx.Equal("wikitext2-raw-test@abc", projected.PerplexityCorpusId);

        // A failure after that leaves the numbers exactly where they are and only records the reason.
        var failing = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);
        var failingClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkFidelityFailedAsync(run.Id, failingClaim.Version, "llama-perplexity produced no final estimate.").ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var afterFailure = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false);
        AssertEx.Equal<Guid?>(latest, afterFailure.FidelityAttemptId, "A failed re-measurement leaves the projection pointing at the last success.");
        AssertEx.Equal<double?>(6.7983, afterFailure.PerplexityMean);
        AssertEx.Equal("failed", afterFailure.FidelityStatus);
        AssertEx.True(afterFailure.FidelityErrorMessage is not null, "A failure must carry an operator-safe reason.");
        AssertEx.True(failing != afterFailure.FidelityAttemptId, "The failed attempt must not become the projection source.");
    }

    /// <summary>
    ///     Freeze queues NO measurement — it only records the cells that will never be measured. One fidelity item per
    ///     measured CELL, and only once that cell has an answer: perplexity is deterministic given the same weights
    ///     and arguments, so N repeats would produce N identical numbers at N times the cost, and a warm-up is never
    ///     compared at all.
    /// </summary>
    [Test]
    public async Task Freeze_WithFidelityEnabled_QueuesNothingAndMarksTheCellsItWillNeverMeasure()
    {
        await using var context = await CreateSchemaAsync("fidelity-freeze.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var entity = await context.BenchmarkProjects.SingleAsync(candidate => candidate.Id == project.Id).ConfigureAwait(false);
        entity.FidelityEnabled = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var groupId = Guid.NewGuid();
        _ = await store.StartRunsAsync([
                           CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 0, IsWarmup = true },
                           CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 1 },
                           CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 2 }
                       ],
                       project.Version)
                       .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        AssertEx.Empty(await context.BenchmarkWorkItems.AsNoTracking()
                                    .Where(item => item.Kind == BenchmarkWorkKind.Fidelity)
                                    .ToListAsync()
                                    .ConfigureAwait(false));

        var stored = await context.BenchmarkRuns.AsNoTracking().Where(run => run.ProjectId == project.Id).OrderBy(run => run.CreatedAtUtc).ToListAsync().ConfigureAwait(false);
        AssertEx.Equal("skipped", stored.Single(run => run.IsWarmup).FidelityStatus, "A warm-up records that it was skipped, not that it was never asked.");
        AssertEx.Equal<string?>(null, stored.Single(run => run.RepeatIndex == 1).FidelityStatus, "The measured cell is not queued until it has an answer to measure.");
        AssertEx.Equal("skipped", stored.Single(run => run.RepeatIndex == 2).FidelityStatus);
    }

    /// <summary>
    ///     The fidelity work item used to be inserted at freeze, so a primary that failed or was cancelled left hours
    ///     of GPU work queued against a run with no answer — the queue would dutifully measure a corpse.
    /// </summary>
    [Test]
    public async Task Fidelity_IsSeededOnPrimarySuccessAndOnNoOtherOutcome()
    {
        await using var context = await CreateSchemaAsync("fidelity-on-success.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var entity = await context.BenchmarkProjects.SingleAsync(candidate => candidate.Id == project.Id).ConfigureAwait(false);
        entity.FidelityEnabled = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var failed = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        var failedClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimaryFailedAsync(failed.Id, failedClaim.Version, "the runtime never became ready").ConfigureAwait(false);

        project = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var cancelled = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        var cancelledClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimaryCancelledAsync(cancelled.Id, cancelledClaim.Version).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        AssertEx.Empty(await context.BenchmarkWorkItems.AsNoTracking()
                                    .Where(item => item.Kind == BenchmarkWorkKind.Fidelity)
                                    .ToListAsync()
                                    .ConfigureAwait(false));
        AssertEx.Empty(await context.BenchmarkFidelityAttempts.AsNoTracking().ToListAsync().ConfigureAwait(false));

        project = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var succeeded = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        var succeededClaim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(succeeded.Id, succeededClaim.Run.Version,
                Encoding.UTF8.GetBytes("[{\"text\":\"answer\"}]"), 1, 4096, 100, 12, 120))
            .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var items = await context.BenchmarkWorkItems.AsNoTracking()
                                 .Where(item => item.Kind == BenchmarkWorkKind.Fidelity)
                                 .ToListAsync()
                                 .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, items.Count, "Exactly the run that produced an answer is measured.");
        AssertEx.Equal(succeeded.Id, items[0].RunId);
        AssertEx.Equal("queued", (await context.BenchmarkRuns.AsNoTracking().SingleAsync(run => run.Id == succeeded.Id).ConfigureAwait(false)).FidelityStatus);
    }

    [Test]
    public async Task EnqueueFidelityAsync_WhileOneIsAlreadyQueued_IsRefused()
    {
        await using var context = await CreateSchemaAsync("fidelity-double-enqueue.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await store.StartRunAsync(CreateRun(project)).ConfigureAwait(false);
        _ = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.EnqueueFidelityAsync(run.Id, "ppl").ConfigureAwait(false);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.EnqueueFidelityAsync(run.Id, "ppl")).ConfigureAwait(false);
        AssertEx.Equal("FidelityAlreadyQueued", conflict.Code);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.EnqueueFidelityAsync(run.Id, "hellaswag")).ConfigureAwait(false);
    }

    /// <summary>
    ///     What a scheduled matrix's busy guard has to ask. Counting a project's RUN statuses called it idle the moment
    ///     every primary finished, while its judging, fidelity and pairwise work was still holding the single-consumer
    ///     queue and the GPU — so the next fire piled a second matrix straight on top of the first.
    /// </summary>
    [Test]
    public async Task CountActiveWorkAsync_CountsEveryKindOfOneProjectAndNothingTerminalOrForeign()
    {
        await using var context = await CreateSchemaAsync("count-active-work.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var runs = await store.StartRunsAsync([CreateRun(project), CreateRun(project)], project.Version).ConfigureAwait(false);
        _ = await store.EnqueueFidelityAsync(runs[0].Id, "ppl").ConfigureAwait(false);
        _ = await InsertComparisonWorkAsync(context, project.Id, revision.Id, runs[0].Id).ConfigureAwait(false);

        // A cancelled run's work is terminal, so it is not what makes a project busy.
        _ = await store.CancelAsync(runs[1].Id, runs[1].Version).ConfigureAwait(false);

        // And another project's queue is not this project's — with foreign keys off, the join is the only thing saying so.
        var (other, _) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        _ = await store.StartRunAsync(CreateRun(other)).ConfigureAwait(false);

        var active = await store.CountActiveWorkAsync(project.Id).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, active.Values.Sum());
        AssertEx.Equal(expected: 1, active[BenchmarkWorkKind.Primary]);
        AssertEx.Equal(expected: 1, active[BenchmarkWorkKind.Fidelity]);
        AssertEx.Equal(expected: 1, active[BenchmarkWorkKind.Comparison]);
        AssertEx.Equal(expected: 1, (await store.CountActiveWorkAsync(other.Id).ConfigureAwait(false)).Values.Sum());
    }

    private async Task<NodeChatDbContext> CreateSchemaAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    /// <summary>
    ///     Inserts a comparison and its work item directly. The pairwise PLANNER that will produce these is S3's; the
    ///     lifecycle they travel through is this slice's, and it has to be exercised before that planner exists.
    /// </summary>
    private static async Task<Guid> InsertComparisonWorkAsync(NodeChatDbContext context,
        Guid projectId,
        Guid revisionId,
        Guid runId,
        BenchmarkJudgeAttemptStatus status = BenchmarkJudgeAttemptStatus.Queued,
        (Guid Left, Guid Right)? pair = null)
    {
        var (left, right) = pair ?? (new Guid("11111111-0000-0000-0000-000000000000"), new Guid("22222222-0000-0000-0000-000000000000"));
        var comparison = new BenchmarkJudgeComparison
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            PolicyRevisionId = revisionId,
            CohortGeneration = 1,
            TaskInputHash = string.Empty,
            RunAId = left,
            RunBId = right,
            Order = 0,
            AttemptSequence = 1,
            Sequence = 1,
            Status = status,
            EnqueuedAtUtc = 1,
            Version = 1
        };
        context.BenchmarkComparisons.Add(comparison);
        context.BenchmarkWorkItems.Add(new BenchmarkWorkItem
        {
            RunId = runId,
            Kind = BenchmarkWorkKind.Comparison,
            ComparisonId = comparison.Id,
            Status = BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = 2
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        return comparison.Id;
    }

    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision)> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(CreateProject()).ConfigureAwait(false);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyBytes, PolicyHash).ConfigureAwait(false);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)), activation.Revision);
    }

    private static BenchmarkProjectInput CreateProject() =>
        new(Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("{\"task\":\"answer\"}"), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand CreateRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version,
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), "model.gguf", LocalModelOrigin.Imported, "v1:" + new string('a', 64), "Agent", 1, 4096);
}
