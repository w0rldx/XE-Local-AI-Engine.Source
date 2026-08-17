namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
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
    public async Task ListRuns_FiltersByModelGroupAndByScoredWithoutChangingTheRanking()
    {
        await using var context = await CreateDatabaseAsync("rank-filters.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var scored = await ScoredRunAsync(store, project.Id, 60, fingerprint: Fingerprint('a')).ConfigureAwait(false);
        var otherModel = await ScoredRunAsync(store, project.Id, 90, fingerprint: Fingerprint('b')).ConfigureAwait(false);
        var unscored = await StartRunAsync(store, project.Id, Fingerprint('a')).ConfigureAwait(false);

        var sameModel = await store.ListRunsAsync(project.Id, skip: 0, take: 10, Fingerprint('a')).ConfigureAwait(false);
        var scoredOnly = await store.ListRunsAsync(project.Id, skip: 0, take: 10, modelGroupKey: null, includeUnscored: false).ConfigureAwait(false);

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
        string? executionKey)
    {
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        _ = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(revision.Id, new ReadOnlyMemory<byte>(JudgeRuntime))
        }).ConfigureAwait(false);
        var judge = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        if (executionKey is not null)
        {
            _ = await store.MarkJudgeLaunchReadyAsync(judge.JudgeAttemptId!.Value, judge.QueueSequence, judge.Version, Receipt(), executionKey)
                           .ConfigureAwait(false);
        }

        _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judge.Version, Encoding.UTF8.GetBytes("{}"), 5, score))
                       .ConfigureAwait(false);
        return run.Id;
    }

    private static async Task<Guid> ScoredRunAsync(BenchmarkStore store, Guid projectId, int score, string? fingerprint = null)
    {
        var runId = await StartRunAsync(store, projectId, fingerprint).ConfigureAwait(false);
        var primary = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(runId, primary.Run.Version)).ConfigureAwait(false);
        _ = await store.SetUserScoreAsync(runId, score, succeeded.Version).ConfigureAwait(false);
        return runId;
    }

    private static async Task<Guid> StartRunAsync(BenchmarkStore store, Guid projectId, string? fingerprint = null)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false));
        var run = await store.StartRunAsync(NewRun(project) with
        {
            ModelContentFingerprint = fingerprint ?? Fingerprint('a')
        }).ConfigureAwait(false);
        return run.Id;
    }

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new("{}", "{}", new string('e', count: 64), new string('r', count: 64), "identity", "cpu", null, null,
            new string('x', count: 64), false, "auto");

    private static BenchmarkPrimarySuccessCommand PrimarySuccess(Guid runId, long expectedWorkVersion) =>
        new(runId, expectedWorkVersion, Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""), 1, 4096, 10, 12, 120);

    private static string Fingerprint(char value) => "v1:" + new string(value, count: 64);

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
