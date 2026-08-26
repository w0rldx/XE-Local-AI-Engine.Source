namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Ranking inside one project: an operator override always ranks, a judge score ranks only while its judging is in
///     the current cohort, and the rank itself is a property of the project rather than of the page it is read on.
/// </summary>
public sealed class BenchmarkRankingStoreTests : IDisposable
{
    private const string HashA = "0000000000000000000000000000000000000000000000000000000000000001";
    private static readonly byte[] PolicyA = Encoding.UTF8.GetBytes("""{"rubric":"a"}""");
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
    public async Task Rank_IsDenseDescendingAcrossTheWholeProject_NotThePage()
    {
        await using var context = await CreateDatabaseAsync("rank-dense.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var ninety = await ScoredRunAsync(store, project.Id, 90).ConfigureAwait(false);
        var seventyFirst = await ScoredRunAsync(store, project.Id, 70).ConfigureAwait(false);
        var seventySecond = await ScoredRunAsync(store, project.Id, 70).ConfigureAwait(false);
        var ten = await ScoredRunAsync(store, project.Id, 10).ConfigureAwait(false);

        var firstPage = await store.ListRunsAsync(project.Id, skip: 0, take: 2).ConfigureAwait(false);
        var secondPage = await store.ListRunsAsync(project.Id, skip: 2, take: 2).ConfigureAwait(false);

        var ranks = firstPage.Items.Concat(secondPage.Items).ToDictionary(static run => run.Id, static run => run.Rank);
        AssertEx.Equal<int?>(1, ranks[ninety]);
        AssertEx.Equal<int?>(2, ranks[seventyFirst], "Ties share a rank.");
        AssertEx.Equal<int?>(2, ranks[seventySecond]);
        AssertEx.Equal<int?>(3, ranks[ten], "Dense: the next distinct score is the next integer, not the next row number.");
        AssertEx.Equal(expected: 4, AssertEx.NotNull(firstPage.RankCohort).RankedCount);
        AssertEx.Equal(expected: 4, firstPage.RankCohort!.TotalScored);
    }

    [Test]
    public async Task Rank_UserOverrideOutranksTheJudgeAndAlwaysCounts()
    {
        await using var context = await CreateDatabaseAsync("rank-user-override.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var judged = await JudgedRunAsync(store, project, revision, score: 40, executionKey: "key-a").ConfigureAwait(false);

        // A second run the judge never touched, but the operator scored.
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var overridden = await ScoredRunAsync(store, refreshed.Id, 95).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal(BenchmarkQualityScoreSources.User, byId[overridden].QualityScoreSource);
        AssertEx.Equal<int?>(1, byId[overridden].Rank);
        AssertEx.Equal(BenchmarkQualityScoreSources.Judge, byId[judged].QualityScoreSource);
        AssertEx.Equal<int?>(2, byId[judged].Rank);
        AssertEx.Null(byId[judged].Judge!.RankExclusionReason, "A judging in the live cohort ranks.");
    }

    [Test]
    public async Task Rank_ExcludesJudgingsOutsideTheCurrentCohort()
    {
        await using var context = await CreateDatabaseAsync("rank-cohort.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        // Ranked: it defines the cohort key.
        var ranked = await JudgedRunAsync(store, project, revision, score: 80, executionKey: "key-a").ConfigureAwait(false);

        // Judged on a different runtime — same policy, different execution.
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var otherRuntime = await JudgedRunAsync(store, refreshed, revision, score: 99, executionKey: "key-b").ConfigureAwait(false);

        // Judged with no execution key at all — the node could not describe what it ran.
        refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var incomplete = await JudgedRunAsync(store, refreshed, revision, score: 99, executionKey: null).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[ranked].Rank);
        AssertEx.Null(byId[otherRuntime].Rank, "A judging on another judge runtime is not comparable and does not rank.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonExecutionKeyMismatch, byId[otherRuntime].Judge!.RankExclusionReason);
        AssertEx.Null(byId[incomplete].Rank);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonExecutionIdentityIncomplete, byId[incomplete].Judge!.RankExclusionReason);
        AssertEx.Equal<int?>(99, byId[otherRuntime].Judge!.Score, "An unranked judging still shows its score.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 3, page.RankCohort!.TotalScored, "Every judging counts as scored; only one of them ranks.");
    }

    [Test]
    public async Task Rank_ExcludesAStaleCohortGeneration()
    {
        await using var context = await CreateDatabaseAsync("rank-generation.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await JudgedRunAsync(store, project, revision, score: 80, executionKey: "key-a").ConfigureAwait(false);

        // The operator moves the cohort. The old judging keeps its score and its key, but not its membership.
        _ = await store.BeginProjectRejudgeAsync(project.Id, await CurrentVersionAsync(store, project.Id).ConfigureAwait(false)).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var judged = page.Items.Single(item => item.Id == run);
        AssertEx.Null(judged.Rank);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonGenerationStale, judged.Judge!.RankExclusionReason);
        var cohort = AssertEx.NotNull(page.RankCohort);
        AssertEx.Equal(expected: 0, cohort.RankedCount);
        AssertEx.Equal<int?>(2, cohort.CohortGeneration);
    }

    [Test]
    public async Task Rank_ExcludesAJudgingUnderAnOutdatedPolicy()
    {
        await using var context = await CreateDatabaseAsync("rank-policy.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);
        var run = await JudgedRunAsync(store, project, revision, score: 80, executionKey: "key-a").ConfigureAwait(false);

        _ = await store.ActivateJudgePolicyAsync(project.Id,
                           await CurrentVersionAsync(store, project.Id).ConfigureAwait(false),
                           Encoding.UTF8.GetBytes("""{"rubric":"b"}"""),
                           "0000000000000000000000000000000000000000000000000000000000000002")
                       .ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var judged = page.Items.Single(item => item.Id == run);
        AssertEx.Null(judged.Rank);
        var judgeView = AssertEx.NotNull(judged.Judge);
        AssertEx.False(judgeView.PolicyCurrent);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonPolicyOutdated, judgeView.RankExclusionReason);
    }

    [Test]
    public async Task Truncated_OutranksNoScore_OnEveryPathThatReturnsARun()
    {
        // The live bug: on a project WITHOUT a judge the truncated run came back "no-score" — technically true and
        // completely useless, because scoring it is not what it needs. Only LoadRankingAsync applied the run-level
        // exclusions, so the single-run read and every write-returning path still reported the judge-derived reason.
        await using var context = await CreateDatabaseAsync("truncated-beats-no-score.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var runId = await StartRunAsync(store, project.Id).ConfigureAwait(false);
        var claim = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));

        // The write's own return value is the third path, and the one the executor hands straight to the hub.
        var written = await store.MarkPrimarySucceededAsync(PrimarySuccess(runId, claim.Run.Version) with
        {
            PrimaryStopReason = "length"
        }).ConfigureAwait(false);
        var read = AssertEx.NotNull(await store.GetRunAsync(runId).ConfigureAwait(false));
        var listed = (await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false)).Items.Single();

        foreach (var (path, run) in new[]
                 {
                     ("MarkPrimarySucceededAsync", written),
                     ("GetRunAsync", read),
                     ("ListRunsAsync", listed)
                 })
        {
            AssertEx.Equal(BenchmarkRunJudgeStates.ReasonTruncated, run.Judge!.RankExclusionReason,
                $"{path} must report truncation, not the judge-derived reason.");
            AssertEx.Null(run.QualityScore, $"{path} must not give a truncated run a ranking value.");
            AssertEx.Null(run.Rank, $"{path} must not rank a truncated run.");
            AssertEx.Equal(BenchmarkQualityScoreSources.None, run.QualityScoreSource, path);
        }
    }

    [Test]
    public async Task Rank_ExcludesATruncatedRunEvenWhenTheJudgeScoredItWell()
    {
        await using var context = await CreateDatabaseAsync("rank-truncated.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        // Both judgings are in the live cohort and both scored well. Only the complete one is a comparable measurement.
        var complete = await JudgedRunAsync(store, project, revision, score: 80, executionKey: "key-a").ConfigureAwait(false);
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var truncated = await JudgedRunAsync(store, refreshed, revision, score: 96, executionKey: "key-a", stopReason: "length").ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[complete].Rank);
        AssertEx.Null(byId[truncated].Rank, "An answer cut off at the token budget must not outrank a complete one.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonTruncated, byId[truncated].Judge!.RankExclusionReason,
            "Truncation is decided before every judge-based reason: the judging itself is perfectly current.");
        AssertEx.Equal(BenchmarkQualityScoreSources.None, byId[truncated].QualityScoreSource);
        AssertEx.Null(byId[truncated].QualityScore);
        AssertEx.Equal<int?>(96, byId[truncated].Judge!.Score, "The score stays visible; it just does not rank.");
        AssertEx.Equal("length", byId[truncated].PrimaryStopReason);
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, byId[truncated].PrimaryStatus, "Truncation flags a run; it never fails it.");
        AssertEx.Equal("stop", byId[complete].PrimaryStopReason);

        // The denominator must drop the truncated run the same way the ranking does. Counting it left the cohort badge
        // reading "1 of 2 ranked" forever: re-judging cannot un-truncate a run, so nothing the operator does closes
        // that gap.
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored,
            "A judge-scored but truncated run can never be ranked, so it must not grow \"n of m ranked\".");
    }

    [Test]
    public async Task Rank_ExcludesASilentlyIncompleteRunTheSameWayItExcludesATruncatedOne()
    {
        await using var context = await CreateDatabaseAsync("rank-incomplete.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        var complete = await JudgedRunAsync(store, project, revision, score: 70, executionKey: "key-a").ConfigureAwait(false);
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var incomplete = await JudgedRunAsync(store, refreshed, revision, score: 95, executionKey: "key-a",
                                       stopReason: BenchmarkPrimaryStopReasons.Incomplete)
                                   .ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[complete].Rank);
        AssertEx.Null(byId[incomplete].Rank, "A run that answered nothing must not outrank one that answered.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonIncomplete, byId[incomplete].Judge!.RankExclusionReason,
            "The reason must name the absence of an answer, not the judge state, which is perfectly current.");
        AssertEx.Equal(BenchmarkQualityScoreSources.None, byId[incomplete].QualityScoreSource);
        AssertEx.Null(byId[incomplete].QualityScore);
        AssertEx.Equal<int?>(95, byId[incomplete].Judge!.Score, "The score stays visible; it just does not rank.");
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, byId[incomplete].PrimaryStatus, "A silent run is flagged, never failed.");

        // Same denominator rule as truncation: nothing the operator does can make an absent answer gradable.
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored);
    }

    [Test]
    public async Task Rank_UserScoreStillRanksATruncatedRun()
    {
        await using var context = await CreateDatabaseAsync("rank-truncated-override.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);

        // The operator override wins over truncation exactly as it wins over every judge-based exclusion.
        var overridden = await ScoredRunAsync(store, project.Id, 55, stopReason: "length").ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var run = page.Items.Single(item => item.Id == overridden);
        AssertEx.Equal<int?>(1, run.Rank);
        AssertEx.Equal(BenchmarkQualityScoreSources.User, run.QualityScoreSource);
        AssertEx.Null(run.Judge!.RankExclusionReason);
        AssertEx.Equal("length", run.PrimaryStopReason);

        // The other side of the mirror: a truncated run the operator scored anyway DOES rank, so it must also count.
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).TotalScored);
    }

    [Test]
    public async Task Rank_ExcludesAWarmUpRunEvenWhenTheOperatorScoredIt()
    {
        await using var context = await CreateDatabaseAsync("rank-warmup.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);

        // A warm-up is the ONE exclusion an operator score does not override: it exists to absorb the first-launch cost
        // the repeats after it should not pay, so ranking it against them would rank the very thing it controls for.
        var warmup = await ScoredRunAsync(store, project.Id, 95, warmup: true).ConfigureAwait(false);
        var measured = await ScoredRunAsync(store, project.Id, 60).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 10).ConfigureAwait(false);

        var warmupRun = page.Items.Single(item => item.Id == warmup);
        AssertEx.Null(warmupRun.Rank, "A warm-up never ranks.");
        AssertEx.Null(warmupRun.QualityScore, "A warm-up carries no ranking value, even with an operator override.");
        AssertEx.Equal(BenchmarkQualityScoreSources.None, warmupRun.QualityScoreSource);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonWarmup, warmupRun.Judge!.RankExclusionReason);
        AssertEx.True(warmupRun.IsWarmup, "The flag round-trips through the list projection.");
        AssertEx.Equal<int?>(0, warmupRun.RepeatIndex);
        AssertEx.NotNull(warmupRun.RepeatGroupId?.ToString());

        AssertEx.Equal<int?>(1, page.Items.Single(item => item.Id == measured).Rank,
            "The measured run is rank 1 — the warm-up's higher score does not sit above it.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored,
            "A warm-up must not inflate the denominator of \"n of m ranked\" with a run that can never be ranked.");
    }

    [Test]
    public async Task ListRuns_FiltersByModelGroupAndByScoredWithoutChangingTheRanking()
    {
        await using var context = await CreateDatabaseAsync("rank-filters.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var scored = await ScoredRunAsync(store, project.Id, 60, fingerprint: Fingerprint('a')).ConfigureAwait(false);
        var otherModel = await ScoredRunAsync(store, project.Id, 90, fingerprint: Fingerprint('b')).ConfigureAwait(false);
        var unscored = await StartRunAsync(store, project.Id, Fingerprint('a')).ConfigureAwait(false);

        var sameModel = await store.ListRunsAsync(project.Id, skip: 0, take: 10, Fingerprint('a')).ConfigureAwait(false);
        var scoredOnly = await store.ListRunsAsync(project.Id, skip: 0, take: 10, modelContentFingerprint: null, includeUnscored: false).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, sameModel.TotalCount, "Same-model history counts only that model's runs.");
        AssertEx.True(sameModel.Items.Any(item => item.Id == scored) && sameModel.Items.Any(item => item.Id == unscored));
        AssertEx.Equal<int?>(2, sameModel.Items.Single(item => item.Id == scored).Rank,
            "Rank is a property of the project, so a filtered page does not renumber it.");
        AssertEx.Equal(expected: 2, scoredOnly.TotalCount);
        AssertEx.False(scoredOnly.Items.Any(item => item.Id == unscored), "includeUnscored=false drops runs with no quality score.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonNoScore,
            sameModel.Items.Single(item => item.Id == unscored).Judge!.RankExclusionReason);
        _ = otherModel;
    }

    [Test]
    public async Task VerifiedJudging_JoinsTheCohortOnTheSentinelKeyAndRanks()
    {
        // A judging whose rubric was entirely verified server-side has no runtime to describe — which is not the same
        // as having an incomplete description of one, and execution-identity-incomplete would unrank it forever. The
        // constant key makes every such attempt of one revision share a cohort deterministically.
        await using var context = await CreateDatabaseAsync("rank-verified-sentinel.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        var high = await JudgedRunAsync(store, project, revision, score: 90, executionKey: null,
            verifiedExecutionKey: BenchmarkJudgeExecutionKey.VerifiedSentinel).ConfigureAwait(false);
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var low = await JudgedRunAsync(store, refreshed, revision, score: 40, executionKey: null,
            verifiedExecutionKey: BenchmarkJudgeExecutionKey.VerifiedSentinel).ConfigureAwait(false);

        var runs = await store.ListRunsAsync(project.Id, skip: 0, take: 200).ConfigureAwait(false);
        var byId = runs.Items.ToDictionary(static run => run.Id);
        var current = AssertEx.NotNull(await store.GetJudgePolicyRevisionAsync(revision.Id).ConfigureAwait(false));

        AssertEx.Equal(BenchmarkJudgeExecutionKey.VerifiedSentinel, current.ReferenceExecutionKey,
            "The first success claims the cohort, exactly as a measured key does.");
        AssertEx.Equal<int?>(expected: 1, byId[high].Rank);
        AssertEx.Equal<int?>(expected: 2, byId[low].Rank);
        AssertEx.Null(byId[high].Judge?.RankExclusionReason);
        AssertEx.True(AssertEx.NotNull(byId[low].Judge).ExecutionCurrent);
    }

    [Test]
    public async Task VerifiedSentinel_NeverOverwritesAMeasuredExecutionKey()
    {
        // The key is written once, at launch. A success command carrying the sentinel must not be able to repair or
        // replace a measured identity — that is how two different executions would end up in one cohort.
        await using var context = await CreateDatabaseAsync("rank-verified-no-overwrite.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var (project, revision) = await CreateJudgeProjectAsync(store).ConfigureAwait(false);

        var measured = await JudgedRunAsync(store, project, revision, score: 70, executionKey: "measured-key",
            verifiedExecutionKey: BenchmarkJudgeExecutionKey.VerifiedSentinel).ConfigureAwait(false);

        var runs = await store.ListRunsAsync(project.Id, skip: 0, take: 200).ConfigureAwait(false);
        var judge = AssertEx.NotNull(runs.Items.Single(run => run.Id == measured).Judge);

        AssertEx.Equal("measured-key", judge.ExecutionKey);
        AssertEx.Equal("measured-key", AssertEx.NotNull(await store.GetJudgePolicyRevisionAsync(revision.Id).ConfigureAwait(false)).ReferenceExecutionKey);
    }

    private static async Task<long> CurrentVersionAsync(BenchmarkStore store, Guid projectId) =>
        AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false)).Version;

    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision)> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA).ConfigureAwait(false);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)), activation.Revision);
    }

    /// <summary>A run judged to <paramref name="score" /> on the execution <paramref name="executionKey" /> describes.</summary>
    private static async Task<Guid> JudgedRunAsync(BenchmarkStore store,
        BenchmarkProjectRecord project,
        BenchmarkJudgePolicyRevisionRecord revision,
        int score,
        string? executionKey,
        string stopReason = "stop",
        string? verifiedExecutionKey = null)
    {
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            PrimaryStopReason = stopReason,
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, new ReadOnlyMemory<byte>(JudgeRuntime))
        }).ConfigureAwait(false);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        if (executionKey is not null)
        {
            _ = await store.MarkJudgeLaunchReadyAsync(judge.JudgeAttemptId!.Value, judge.QueueSequence, judge.Version, Receipt(), executionKey)
                           .ConfigureAwait(false);
        }

        _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id,
                           judge.Version,
                           Encoding.UTF8.GetBytes("{}"),
                           5,
                           score,
                           verifiedExecutionKey))
                       .ConfigureAwait(false);
        return run.Id;
    }

    [Test]
    public async Task ListAllRuns_ReturnsEveryRunWithTheSameRankingThePagedReadGives()
    {
        // The export used to page, which recomputed the whole-project ranking per page to produce the same answer each
        // time. Ranking once is only safe while the one-call read is INDISTINGUISHABLE from the paged one.
        await using var context = await CreateDatabaseAsync("rank-list-all.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var high = await ScoredRunAsync(store, project.Id, score: 90).ConfigureAwait(false);
        var low = await ScoredRunAsync(store, project.Id, score: 10).ConfigureAwait(false);
        var unranked = await ScoredRunAsync(store, project.Id, score: 50, warmup: true).ConfigureAwait(false);

        var all = await store.ListAllRunsAsync(project.Id).ConfigureAwait(false);
        var paged = await store.ListRunsAsync(project.Id, skip: 0, take: 200).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, all.Items.Count, "Every run of the project, in one call.");
        AssertEx.Equal(paged.TotalCount, all.TotalCount);
        AssertEx.True(all.Items.Select(static run => run.Id).SequenceEqual(paged.Items.Select(static run => run.Id)),
            "Same order, so the export's rows do not reshuffle against the table the operator was looking at.");

        var byId = all.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(expected: 1, byId[high].Rank);
        AssertEx.Equal<int?>(expected: 2, byId[low].Rank);
        AssertEx.Null(byId[unranked].Rank, "A warm-up is excluded here exactly as it is on a page.");
        AssertEx.Equal(AssertEx.NotNull(paged.RankCohort).RankedCount, AssertEx.NotNull(all.RankCohort).RankedCount);
        AssertEx.Equal(paged.RankCohort!.TotalScored, all.RankCohort!.TotalScored);
    }

    private static async Task<Guid> ScoredRunAsync(BenchmarkStore store,
        Guid projectId,
        int score,
        string? fingerprint = null,
        string stopReason = "stop",
        bool warmup = false)
    {
        var runId = await StartRunAsync(store, projectId, fingerprint, warmup).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(runId, primary.Run.Version) with
        {
            PrimaryStopReason = stopReason
        }).ConfigureAwait(false);
        _ = await store.SetUserScoreAsync(runId, score, succeeded.Version).ConfigureAwait(false);
        return runId;
    }

    private static async Task<Guid> StartRunAsync(BenchmarkStore store, Guid projectId, string? fingerprint = null, bool warmup = false)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false));
        var run = await store.StartRunAsync(NewRun(project) with
        {
            ModelContentFingerprint = fingerprint ?? Fingerprint('a'),
            RepeatGroupId = warmup ? Guid.NewGuid() : null,
            RepeatIndex = warmup ? 0 : null,
            IsWarmup = warmup
        }).ConfigureAwait(false);
        return run.Id;
    }

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new("{}", "{}", new string('e', count: 64), new string('r', count: 64), "identity", "cpu", null, null,
            new string('x', count: 64), false, "auto");

    private static BenchmarkPrimarySuccessCommand PrimarySuccess(Guid runId, long expectedWorkVersion) =>
        new(runId, expectedWorkVersion, Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""), 1, 4096, 10, 12, 120);

    private static string Fingerprint(char value) =>
        "v1:" + new string(value, count: 64);

    private static BenchmarkProjectInput NewProject() =>
        new(Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("""{"task":"answer"}"""), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""), "model.gguf",
            LocalModelOrigin.Imported, Fingerprint('a'), "Agent", 1, 4096);

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }
}
