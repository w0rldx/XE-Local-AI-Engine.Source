namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The pairwise half of the store: enqueue, terminalize, publish, read. The two invariants worth stating are that
///     a fit is ONE row with ONE active pointer — so a crash cannot leave a ranking blended from two fits — and that
///     the ranking decides staleness from the fit row and the revision row alone, never by re-reading verdicts.
/// </summary>
public sealed class BenchmarkPairwiseStoreTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ExecutionKey = "exec-key-v1";
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);
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
    public async Task EnsureComparisonsAsync_CreatesBothOrdersOfEverySlot_AndBumpsTheComparisonSetVersion()
    {
        await using var context = await CreateSchemaAsync("ensure-pairs.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 3).ConfigureAwait(false);

        var created = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);

        AssertEx.Equal(expected: 6, created, "Three runs make three unordered pairs, and every pair is judged both ways round.");
        var cohort = await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 6, cohort.Comparisons.Count);
        AssertEx.True(cohort.Comparisons.All(static comparison => comparison.RunAId.CompareTo(comparison.RunBId) < 0),
            "Every stored pair is in canonical order — the CHECK enforces it, and the planner must already agree.");
        AssertEx.Equal(expected: 1, cohort.ComparisonSetVersion, "Creating a cohort moves the set version exactly once.");
        AssertEx.Equal<Guid?>(revision.Id, cohort.PolicyRevisionId);

        // A second pass has nothing to add: the live-slot filter already covers every slot.
        AssertEx.Equal(expected: 0, await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion,
            "An idempotent pass must not move the set version either.");
    }

    [Test]
    public async Task EnsureComparisonsAsync_AfterAFailedComparison_ReEnqueuesThatSlotAtTheNextAttemptSequence()
    {
        await using var context = await CreateSchemaAsync("ensure-pairs-retry.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);

        var claimed = await ClaimComparisonAsync(store).ConfigureAwait(false);
        await store.MarkComparisonFailedAsync(claimed.QueueSequence, claimed.Version, "the judge invocation failed").ConfigureAwait(false);

        var created = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, created, "A terminal-failed slot is free again — that is what the status-filtered live index is for.");
        var cohort = await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false);
        var slot = cohort.Comparisons.Where(comparison => comparison.Order == claimed.Order).OrderBy(comparison => comparison.AttemptSequence).ToArray();
        AssertEx.Equal(expected: 2, slot.Length);
        AssertEx.Equal(expected: 1, slot[0].AttemptSequence);
        AssertEx.Equal(expected: 2, slot[1].AttemptSequence, "The retry is a new row at the next attempt sequence, not a resurrection of the failed one.");
    }

    [Test]
    public async Task ComparisonSetVersion_MovesOnEveryTerminalization_SoAFitOverARebuiltSetIsStale()
    {
        await using var context = await CreateSchemaAsync("comparison-set-version.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var afterInsert = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;

        var first = await ClaimComparisonAsync(store).ConfigureAwait(false);
        await store.MarkComparisonSucceededAsync(new BenchmarkComparisonSuccessCommand(first.QueueSequence, first.Version, "a", null, false, false))
                   .ConfigureAwait(false);
        var second = await ClaimComparisonAsync(store).ConfigureAwait(false);
        await store.MarkComparisonCancelledAsync(second.QueueSequence, second.Version).ConfigureAwait(false);

        var afterTerminalizations = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;
        AssertEx.Equal(afterInsert + 2, afterTerminalizations, "Inserting and terminalizing are the only two ways the set changes, and both move the counter.");
    }

    [Test]
    public async Task MarkComparisonSucceededAsync_ClaimsTheCohortForItsExecutionKey()
    {
        await using var context = await CreateSchemaAsync("comparison-promotes.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var claimed = await ClaimComparisonAsync(store).ConfigureAwait(false);
        _ = await store.MarkComparisonLaunchReadyAsync(claimed.ComparisonId, claimed.QueueSequence, claimed.Version, Receipt(), ExecutionKey)
                       .ConfigureAwait(false);

        await store.MarkComparisonSucceededAsync(new BenchmarkComparisonSuccessCommand(claimed.QueueSequence, claimed.Version, "tie", null, false, false))
                   .ConfigureAwait(false);

        // A pairwise cohort has no judge attempts to claim the reference key, so a comparison must do it — or every
        // fit over the cohort refuses as execution-identity-incomplete and nothing is ever rankable.
        var promoted = AssertEx.NotNull(await store.GetJudgePolicyRevisionAsync(revision.Id).ConfigureAwait(false));
        AssertEx.Equal(ExecutionKey, promoted.ReferenceExecutionKey);
    }

    [Test]
    public async Task PublishPairwiseFitAsync_SwitchesThePointerInOneTransaction_AndRefusesADuplicateKey()
    {
        await using var context = await CreateSchemaAsync("publish-fit.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);

        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:first", setVersion: 1, runs)).ConfigureAwait(false));
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:second", setVersion: 2, runs)).ConfigureAwait(false));

        context.ChangeTracker.Clear();
        var stored = await context.BenchmarkPairwiseFits.AsNoTracking().Where(fit => fit.ProjectId == project.Id).ToListAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, stored.Count, "A fit is immutable: publishing a new one keeps the old row as history.");
        AssertEx.Equal(expected: 1, stored.Count(fit => fit.IsActive), "At most one active fit per scope — the filtered unique index makes two unrepresentable.");
        AssertEx.Equal("v1:second", AssertEx.NotNull(await store.GetActivePairwiseFitAsync(project.Id).ConfigureAwait(false)).FitKey);

        // A racing second terminalization recomputes the SAME key from the same inputs. It must no-op, not mint a twin.
        AssertEx.False(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:second", setVersion: 2, runs)).ConfigureAwait(false));
        AssertEx.Equal("v1:second", AssertEx.NotNull(await store.GetActivePairwiseFitAsync(project.Id).ConfigureAwait(false)).FitKey,
            "The duplicate is swallowed and the standing fit is left exactly as it was.");
    }

    [Test]
    public async Task Ranking_PairwiseMode_ReadsScoresFromTheActiveFit_AndAnOperatorScoreStillWins()
    {
        await using var context = await CreateSchemaAsync("ranking-pairwise.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 3).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var setVersion = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:ranked", setVersion, runs, [69, 50, 31])).ConfigureAwait(false));

        var ranked = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        var top = ranked.Items.Single(run => run.Id == runs[0]);
        AssertEx.Equal<int?>(69, top.QualityScore);
        AssertEx.Equal(BenchmarkQualityScoreSources.Pairwise, top.QualityScoreSource);
        AssertEx.Equal<int?>(1, top.Rank);
        AssertEx.Equal<int?>(3, ranked.Items.Single(run => run.Id == runs[2]).Rank, "The dense rank is over the fitted strengths.");
        AssertEx.Null(top.Judge?.RankExclusionReason, "A run the fit scored is ranked, with nothing to explain.");

        // The operator override outranks the fit exactly as it outranks a pointwise judge score.
        var overridden = await store.SetUserScoreAsync(runs[2], 100, ranked.Items.Single(run => run.Id == runs[2]).Version).ConfigureAwait(false);
        var afterOverride = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal<int?>(100, afterOverride.Items.Single(run => run.Id == overridden.Id).QualityScore);
        AssertEx.Equal(BenchmarkQualityScoreSources.User, afterOverride.Items.Single(run => run.Id == overridden.Id).QualityScoreSource);
    }

    [Test]
    public async Task Ranking_ActiveFitWasFitOverAnOlderComparisonSet_IsNotRankedAndReadsPairwiseStale()
    {
        await using var context = await CreateSchemaAsync("ranking-stale.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var setVersion = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:stale", setVersion, runs, [70, 30])).ConfigureAwait(false));

        // One comparison terminalizes, so the set the fit covered is no longer the set on disk.
        var claimed = await ClaimComparisonAsync(store).ConfigureAwait(false);
        await store.MarkComparisonFailedAsync(claimed.QueueSequence, claimed.Version, "interrupted").ConfigureAwait(false);

        var ranked = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        foreach (var run in ranked.Items)
        {
            AssertEx.Null(run.QualityScore, "A fit over a set that has moved must not rank anything.");
            AssertEx.Equal(BenchmarkRunJudgeStates.ReasonPairwiseStale, run.Judge?.RankExclusionReason);
        }
    }

    [Test]
    public async Task Ranking_RunAbsentFromTheActiveFit_ReadsPairwiseInsufficient()
    {
        await using var context = await CreateSchemaAsync("ranking-absent.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 3).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var setVersion = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:partial", setVersion, [runs[0], runs[1]], [60, 40])).ConfigureAwait(false));

        var ranked = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        var stranded = ranked.Items.Single(run => run.Id == runs[2]);
        AssertEx.Null(stranded.QualityScore);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonPairwiseInsufficient, stranded.Judge?.RankExclusionReason);
        AssertEx.Equal<int?>(60, ranked.Items.Single(run => run.Id == runs[0]).QualityScore, "The runs the fit did cover still rank.");
    }

    [Test]
    public async Task Ranking_PairwiseCohortWithNoFitYet_ReadsPairwisePending()
    {
        await using var context = await CreateSchemaAsync("ranking-pending.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);

        var ranked = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        AssertEx.True(ranked.Items.All(run => run.Judge?.RankExclusionReason == BenchmarkRunJudgeStates.ReasonPairwisePending),
            "Comparisons enqueued and no fit yet is a cohort still judging, and it says so rather than reading as unscored.");
    }

    [Test]
    public async Task Ranking_ProjectThatNeverEnqueuedAComparison_StaysOnThePointwisePath()
    {
        await using var context = await CreateSchemaAsync("ranking-pointwise.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _, runs) = await SeedCohortAsync(store, runCount: 1).ConfigureAwait(false);

        var ranked = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);

        // ComparisonSetVersion is still 0, which is how the read knows the project judges pointwise without decrypting
        // a policy blob per page. The reason is therefore the POINTWISE vocabulary — here the seeded attempt's own
        // failure, because this activation resolved no judge runtime — and never a pairwise one.
        var reason = ranked.Items.Single(run => run.Id == runs[0]).Judge?.RankExclusionReason;
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgeFailed, reason);
        AssertEx.False(reason?.StartsWith("pairwise-", StringComparison.Ordinal) == true, "A pointwise project must never read a pairwise reason.");
    }

    [Test]
    public async Task DeleteRunAsync_WhileAComparisonNamingItIsQueued_IsRefusedFromEitherSideOfThePair()
    {
        await using var context = await CreateSchemaAsync("delete-live-comparison.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, _, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        var (runA, runB) = runs[0].CompareTo(runs[1]) < 0 ? (runs[0], runs[1]) : (runs[1], runs[0]);

        // The B side is the one the old guard could not see: a comparison's work item names only the canonical FIRST
        // run, so deleting B walked straight past "is anything queued for this run".
        var refusedB = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => DeleteAsync(store, runB)).ConfigureAwait(false);
        var refusedA = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => DeleteAsync(store, runA)).ConfigureAwait(false);

        AssertEx.Equal("ActiveRun", refusedB.Code);
        AssertEx.Equal("ActiveRun", refusedA.Code);
        AssertEx.Equal(expected: 2, (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).Comparisons.Count);
    }

    [Test]
    public async Task DeleteRunAsync_OfAFittedParticipant_RemovesItsComparisonsAndRetiresTheFit()
    {
        await using var context = await CreateSchemaAsync("delete-fitted-participant.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 3).ConfigureAwait(false);
        _ = await store.EnsureComparisonsAsync(project.Id, Slots(runs), Runtime(), null).ConfigureAwait(false);
        for (var index = 0; index < 6; index++)
        {
            var claimed = await ClaimComparisonAsync(store).ConfigureAwait(false);
            await store.MarkComparisonSucceededAsync(new BenchmarkComparisonSuccessCommand(claimed.QueueSequence, claimed.Version, "a", null, false, false))
                       .ConfigureAwait(false);
        }

        var beforeVersion = (await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false)).ComparisonSetVersion;
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:fitted", beforeVersion, runs, [70, 50, 30])).ConfigureAwait(false));

        await DeleteAsync(store, runs[2]).ConfigureAwait(false);

        var cohort = await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, cohort.Candidates.Count);
        AssertEx.Equal(expected: 2, cohort.Comparisons.Count, "Only the surviving pair's two orders are left; four comparisons named the deleted run.");
        AssertEx.True(cohort.Comparisons.All(comparison => comparison.RunAId != runs[2] && comparison.RunBId != runs[2]),
            "Foreign keys are off, so a comparison naming a deleted run would simply sit there forever.");
        AssertEx.True(cohort.ComparisonSetVersion > beforeVersion, "The set the fit covered has changed, and staleness is that one integer.");

        // The published fit ranked a run that no longer exists, so it is retired rather than left stale-but-active:
        // the next planner pass re-fits the cohort that is actually left.
        AssertEx.Null(await store.GetActivePairwiseFitAsync(project.Id).ConfigureAwait(false));
        context.ChangeTracker.Clear();
        AssertEx.Empty(await context.BenchmarkWorkItems.AsNoTracking()
                                    .Where(item => item.Kind == BenchmarkWorkKind.Comparison && item.RunId == runs[2])
                                    .ToListAsync()
                                    .ConfigureAwait(false));

        // Re-plannable: the surviving pair is already covered both ways round, so a planner pass adds nothing new.
        AssertEx.Equal(expected: 0, await store.EnsureComparisonsAsync(project.Id, Slots([runs[0], runs[1]]), Runtime(), null).ConfigureAwait(false));
    }

    [Test]
    public async Task DeleteRunAsync_OnAPointwiseProject_TouchesNoPairwiseState()
    {
        await using var context = await CreateSchemaAsync("delete-pointwise.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision, runs) = await SeedCohortAsync(store, runCount: 2).ConfigureAwait(false);
        AssertEx.True(await store.PublishPairwiseFitAsync(Fit(project, revision, "v1:untouched", setVersion: 1, runs)).ConfigureAwait(false));

        await DeleteAsync(store, runs[1]).ConfigureAwait(false);

        // No comparison ever named this run, so nothing pairwise is invalidated by its removal.
        AssertEx.Equal("v1:untouched", AssertEx.NotNull(await store.GetActivePairwiseFitAsync(project.Id).ConfigureAwait(false)).FitKey);
    }

    private static async Task DeleteAsync(BenchmarkStore store, Guid runId) =>
        await store.DeleteRunAsync(runId, AssertEx.NotNull(await store.GetRunAsync(runId).ConfigureAwait(false)).Version).ConfigureAwait(false);

    /// <summary>
    ///     A fitted score lives in the fit row and nowhere else. Per-run copies were the design that let a crash
    ///     between run four and run five leave a ranking blended from two fits, every row internally consistent and the
    ///     order wrong — so the columns must not come back by accident.
    /// </summary>
    [Test]
    public void BenchmarkRun_HasNoPairwiseColumns()
    {
        var offenders = typeof(BenchmarkRun).GetProperties()
                                            .Where(static property => property.Name.Contains("Pairwise", StringComparison.OrdinalIgnoreCase))
                                            .Select(static property => property.Name)
                                            .ToArray();

        AssertEx.Empty(offenders, $"BenchmarkRun must carry no pairwise columns, found: {string.Join(", ", offenders)}");
    }

    private async Task<NodeChatDbContext> CreateSchemaAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    /// <summary>A judged project whose runs all succeeded with stored output — the eligible set a cohort pairs.</summary>
    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision, Guid[] Runs)> SeedCohortAsync(
        BenchmarkStore store,
        int runCount)
    {
        var created = await store.CreateProjectAsync(CreateProject()).ConfigureAwait(false);
        var activation = await store.ActivateJudgePolicyAsync(created.Id, created.Version, PolicyBytes, PolicyHash).ConfigureAwait(false);
        var project = AssertEx.NotNull(await store.GetProjectAsync(created.Id).ConfigureAwait(false));
        List<Guid> runs = [];
        for (var index = 0; index < runCount; index++)
        {
            var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
            var run = await store.StartRunAsync(CreateRun(current)).ConfigureAwait(false);
            var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
            _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id, claimed.Run.Version,
                    Encoding.UTF8.GetBytes("[{\"kind\":\"output\",\"content\":\"answer\"}]"), index + 1, 4096, 100, 12, 120))
                .ConfigureAwait(false);
            runs.Add(run.Id);
        }

        // The candidate order the planner pairs in is (CreatedAtUtc, Id); the assertions above name runs by that order.
        var cohort = await store.GetPairwiseCohortAsync(project.Id).ConfigureAwait(false);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)),
            activation.Revision,
            [.. cohort.Candidates.Select(static candidate => candidate.RunId)]);
    }

    private static BenchmarkPairwiseSlot[] Slots(IReadOnlyList<Guid> runs)
    {
        List<BenchmarkPairwiseSlot> slots = [];
        for (var first = 0; first < runs.Count; first++)
        {
            for (var second = first + 1; second < runs.Count; second++)
            {
                var (runA, runB) = runs[first].CompareTo(runs[second]) < 0 ? (runs[first], runs[second]) : (runs[second], runs[first]);
                slots.Add(new BenchmarkPairwiseSlot(runA, runB, null, string.Empty));
            }
        }

        return [.. slots];
    }

    private static async Task<(long QueueSequence, long Version, Guid ComparisonId, int Order)> ClaimComparisonAsync(BenchmarkStore store)
    {
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        AssertEx.Equal(BenchmarkWorkKind.Comparison, claimed.Kind);
        var comparisonId = claimed.ComparisonId ?? throw new InvalidOperationException("A claimed comparison must name its comparison.");
        var comparison = AssertEx.NotNull(await store.GetComparisonAsync(comparisonId).ConfigureAwait(false));
        return (claimed.QueueSequence, claimed.Version, comparison.Id, comparison.Order);
    }

    private static BenchmarkPairwiseFitCommand Fit(BenchmarkProjectRecord project,
        BenchmarkJudgePolicyRevisionRecord revision,
        string fitKey,
        int setVersion,
        IReadOnlyList<Guid> runs,
        IReadOnlyList<int>? scores = null) =>
        new(project.Id,
            revision.Id,
            revision.CohortGeneration,
            null,
            fitKey,
            string.Empty,
            setVersion,
            "[]",
            JsonSerializer.Serialize(runs.Select((run, index) => new BenchmarkPairwiseScoreEntry(run,
                    scores is null ? 50 : scores[index],
                    null,
                    null,
                    2,
                    1000,
                    null)),
                ScoreOptions),
            Iterations: 12,
            BootstrapReplicates: 1000);

    private static ReadOnlyMemory<byte> Runtime() =>
        Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new("{\"receipt\":true}", "{\"facts\":true}", "facts-hash", "receipt-hash", "identity", "cuda", 32, 32, "sha", true, "auto");

    private static BenchmarkProjectInput CreateProject() =>
        new(Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("{\"task\":\"answer\"}"), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand CreateRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version,
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), "model.gguf", LocalModelOrigin.Imported, "v1:" + new string('a', 64), "Agent", 1, 4096);
}
