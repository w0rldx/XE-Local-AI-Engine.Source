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

    [Test]
    public async Task Recovery_RunningFidelityAttempt_TerminalizesFailedAndLeavesTheProjectionAlone()
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
    ///     One fidelity item per measured CELL. Perplexity is deterministic given the same weights and arguments, so
    ///     N repeats would produce N identical numbers at N times the cost, and a warm-up is never compared at all.
    /// </summary>
    [Test]
    public async Task Freeze_WithFidelityEnabled_MeasuresOneCellAndSkipsWarmupsAndExtraRepeats()
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
        var runs = await store.StartRunsAsync([
                                  CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 0, IsWarmup = true },
                                  CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 1 },
                                  CreateRun(project) with { RepeatGroupId = groupId, RepeatIndex = 2 }
                              ],
                              project.Version)
                              .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        var fidelityItems = await context.BenchmarkWorkItems.AsNoTracking()
                                         .Where(item => item.Kind == BenchmarkWorkKind.Fidelity)
                                         .ToListAsync()
                                         .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, fidelityItems.Count, "Exactly the first measured repeat is measured.");
        AssertEx.Equal(runs[1].Id, fidelityItems[0].RunId);

        var stored = await context.BenchmarkRuns.AsNoTracking().Where(run => run.ProjectId == project.Id).OrderBy(run => run.CreatedAtUtc).ToListAsync().ConfigureAwait(false);
        AssertEx.Equal("skipped", stored.Single(run => run.IsWarmup).FidelityStatus, "A warm-up records that it was skipped, not that it was never asked.");
        AssertEx.Equal("queued", stored.Single(run => run.RepeatIndex == 1).FidelityStatus);
        AssertEx.Equal("skipped", stored.Single(run => run.RepeatIndex == 2).FidelityStatus);
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
        BenchmarkJudgeAttemptStatus status = BenchmarkJudgeAttemptStatus.Queued)
    {
        var left = new Guid("11111111-0000-0000-0000-000000000000");
        var right = new Guid("22222222-0000-0000-0000-000000000000");
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
