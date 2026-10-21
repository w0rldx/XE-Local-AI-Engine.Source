namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Ranking over CELLS: one model, one KV type, one repeat of the whole task-item suite. A cell ranks only when
///     every scorable item in it produced a rankable score, and the two identity stamps a run carries decide whether
///     it still answers a question — and a suite — the project recognises.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkCellRankingStoreTests : IDisposable
{
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
    public async Task ThreeItemsOneRepeat_AreOneRankedCell_ScoredAsTheMeanOfTheirItems()
    {
        // The ordinary way an operator runs a suite. Deriving the cell from the repeat group alone made every one of
        // these three runs its own singleton cell, each missing two of three items, and the project ranked nothing.
        await using var context = await CreateDatabaseAsync("cell-mean.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 3);

        var cell = await ScoredCellAsync(store, project.Id, 90, 60, 30);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.All(runId => byId[runId].Rank == 1), "Every run of the cell reports its cell's rank.");
        AssertEx.True(cell.All(runId => byId[runId].CellQuality == 60), "The cell scores the mean of its per-item scores.");
        AssertEx.Equal<int?>(90, byId[cell[0]].QualityScore, "A run keeps its OWN score; only the rank is the cell's.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount, "One cell, not three runs.");
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored);
    }

    [Test]
    public async Task ACellWithOneUnrankableItem_IsUnranked_ItemIncomplete()
    {
        // Exclusion, not partial credit: scored on the two easy items only, a model that ran out of budget on the
        // hard one would outrank one that attempted everything.
        await using var context = await CreateDatabaseAsync("cell-incomplete.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 3);

        var cell = await ScoredCellAsync(store, project.Id, [(90, "stop"), (60, "stop"), (null, "length")]);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.All(runId => byId[runId].Rank is null), "No run of an incomplete cell ranks.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonTruncated, byId[cell[2]].Judge!.RankExclusionReason,
            "The truncated run keeps its own, more specific reason.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemIncomplete, byId[cell[0]].Judge!.RankExclusionReason,
            "A run nothing about itself excludes reports why its CELL does not rank.");
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 0, page.RankCohort!.TotalScored, "A cell nothing can ever rank stays out of the denominator.");
    }

    [Test]
    public async Task TwoFreezesOfOneProject_AreDistinctCells_NeverAveragedTogether()
    {
        await using var context = await CreateDatabaseAsync("cell-two-freezes.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);

        var first = await ScoredCellAsync(store, project.Id, 90, 90);
        var second = await ScoredCellAsync(store, project.Id, 10, 10);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(90, byId[first[0]].CellQuality);
        AssertEx.Equal<int?>(10, byId[second[0]].CellQuality, "Two freezes are two cells; averaging them would be a wrong number, silently.");
        AssertEx.Equal<int?>(1, byId[first[0]].Rank);
        AssertEx.Equal<int?>(2, byId[second[0]].Rank);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task TwoAgentVariantsOfOneModel_AreTwoCells_BothRankedAndEachNamingItsAgent()
    {
        await using var context = await CreateDatabaseAsync("cell-agent-variants.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);
        var variant = Guid.NewGuid();

        var projectAgentCell = await ScoredCellAsync(store, project.Id, 80, 80);
        var variantCell = await VariantCellAsync(store, project.Id, variant, "Variant", 40, 40);

        var page = await store.ListCellsAsync(project.Id);

        AssertEx.Equal(expected: 2, page.Cells.Count, "One model run under two agent variants is two cells.");
        var first = page.Cells.Single(cell => cell.Items.Any(item => item.RunId == projectAgentCell[0]));
        var second = page.Cells.Single(cell => cell.Items.Any(item => item.RunId == variantCell[0]));
        AssertEx.Equal(project.AgentDefinitionId, first.AgentDefinitionId, "A command naming no agent is stamped with the project's.");
        AssertEx.Equal("Agent", first.AgentName);
        AssertEx.Equal(variant, second.AgentDefinitionId);
        AssertEx.Equal("Variant", second.AgentName);
        AssertEx.Equal(first.PrimaryModelName, second.PrimaryModelName);
        AssertEx.Equal<int?>(1, first.Rank);
        AssertEx.Equal<int?>(2, second.Rank);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task WarmupRuns_AreNotGroupedIntoACell_NorCounted()
    {
        // A warm-up sits at repeat index 0, so it forms a cell that could only ever complete if every leaf item also
        // got a warm-up run — and it would then sit in the denominator forever.
        await using var context = await CreateDatabaseAsync("cell-warmup.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);
        var warmupCell = "cell:" + Guid.NewGuid().ToString("D") + ":0";
        _ = await ScoredCellAsync(store, project.Id, [(50, "stop")], cellKey: warmupCell, warmup: true, itemLimit: 1);

        var measured = await ScoredCellAsync(store, project.Id, 80, 80);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[measured[0]].Rank, "The measured cell is complete and ranks.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored, "The warm-up's one-item cell is not a permanently incomplete denominator entry.");
    }

    [Test]
    public async Task LegacyRunsWithNoTaskItem_RankExactlyAsBefore_EvenAfterItemZeroIsMaterialized()
    {
        // A project frozen before task suites keeps ranking after something materializes its item 0. The materialized
        // item must not turn every historical singleton into an incomplete cell.
        await using var context = await CreateDatabaseAsync("cell-legacy.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await store.CreateProjectAsync(NewProject());
        var high = await LegacyScoredRunAsync(store, project.Id, 90);
        var low = await LegacyScoredRunAsync(store, project.Id, 10);

        _ = await store.GetOrCreateItemsAsync(project.Id);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[high].Rank);
        AssertEx.Equal<int?>(2, byId[low].Rank);
        AssertEx.Null(byId[high].Judge!.RankExclusionReason, "A pre-suite run is never item-revised or item-set-revised.");
        AssertEx.Equal(expected: 2, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task EditingAnItem_ExcludesItsStoredAnswer_AndAUserScoreDoesNotRescueIt()
    {
        // The user-score override is correct for truncation — an operator who read a truncated answer and scored it
        // anyway overruled the machine about a fact they could see — and wrong here: the score was given for a
        // question that has since changed, and the operator has no way to know it did.
        await using var context = await CreateDatabaseAsync("cell-item-revised.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);
        var cell = await ScoredCellAsync(store, project.Id, 90, 90);
        var items = await store.ListTaskItemsAsync(project.Id);

        _ = await store.UpdateTaskItemAsync(project.Id, items[0].Id, items[0].Version, Prompt("rewritten question"));

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemRevised, byId[cell[0]].Judge!.RankExclusionReason,
            "The more specific cause wins: the question changed, not merely the suite around it.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemSetRevised, byId[cell[1]].Judge!.RankExclusionReason,
            "Its sibling's question is untouched, but the suite its cell mean is a mean OF has moved.");
        AssertEx.True(cell.All(runId => byId[runId].Rank is null), "The historical cell no longer ranks.");
        AssertEx.True(cell.All(runId => byId[runId].QualityScore is null), "The operator score does not survive its own question.");
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task UserScore_StillOverridesTruncation()
    {
        // The precedence change must not have broken the override it sits above.
        await using var context = await CreateDatabaseAsync("cell-truncation-override.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);

        var cell = await ScoredCellAsync(store, project.Id, [(70, "length"), (90, "stop")]);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(70, byId[cell[0]].QualityScore, "An operator who scored a truncated answer still overrules the machine.");
        AssertEx.Equal<int?>(80, byId[cell[0]].CellQuality);
        AssertEx.Equal<int?>(1, byId[cell[0]].Rank);
    }

    [Test]
    public async Task AddingAnItem_ExcludesEveryHistoricalCell_ItemSetRevised()
    {
        await using var context = await CreateDatabaseAsync("cell-item-added.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2);
        var cell = await ScoredCellAsync(store, project.Id, 90, 90);
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id));

        _ = await store.CreateTaskItemAsync(project.Id, refreshed.Version, Prompt("a third question"));

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.All(runId => byId[runId].Judge!.RankExclusionReason == BenchmarkRunJudgeStates.ReasonItemSetRevised));
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task DeletingAnItem_DoesNotTurnAPartialCellIntoACompleteOne_EvenWhenItWasUserScored()
    {
        // THE finding. Delete the item a cell never answered and its surviving runs keep matching their own item
        // hashes, satisfy every per-item check, and would constitute a COMPLETE two-item cell whose mean is over a
        // suite the model was never scored on. Only the per-run copy of the set hash can see it — the project-level
        // hash cannot, because it IS the thing that changed.
        await using var context = await CreateDatabaseAsync("cell-item-deleted.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 3);
        var cell = await ScoredCellAsync(store, project.Id, [(90, "stop"), (90, "stop"), (null, "length")]);
        var items = await store.ListTaskItemsAsync(project.Id);

        await store.DeleteTaskItemAsync(project.Id, items[2].Id, items[2].Version);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.Take(2).All(runId => byId[runId].Judge!.RankExclusionReason == BenchmarkRunJudgeStates.ReasonItemSetRevised),
            "The two surviving answers were measured under a suite that no longer exists.");
        AssertEx.True(cell.All(runId => byId[runId].Rank is null), "A two-of-three cell must never rank as a two-of-two one.");
        AssertEx.True(cell.All(runId => byId[runId].CellQuality is null));
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 0, page.RankCohort!.TotalScored);
    }

    [Test]
    public async Task ReorderingItems_LeavesEveryHistoricalCellRanked()
    {
        // The set hash is ordered by item Id, not by index: a reorder changes no question, so a cosmetic drag-and-drop
        // must not unrank a completed suite.
        await using var context = await CreateDatabaseAsync("cell-item-reordered.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 3);
        var cell = await ScoredCellAsync(store, project.Id, 90, 60, 30);
        var items = await store.ListTaskItemsAsync(project.Id);

        _ = await store.ReorderTaskItemsAsync(project.Id, [items[2].Id, items[0].Id, items[1].Id]);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.All(runId => byId[runId].Judge!.RankExclusionReason is null));
        AssertEx.True(cell.All(runId => byId[runId].Rank == 1));
        AssertEx.Equal<int?>(60, byId[cell[0]].CellQuality);
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task Cells_WhenOnlyGapsArePendingReviews_ReportReviewPending()
    {
        await using var context = await CreateDatabaseAsync("cell-review-pending.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2, requiresReview: true);
        int? unscored = null;
        var cell = await ScoredCellAsync(store, project.Id, unscored, unscored);
        _ = await store.SubmitReviewAsync(Approve(AssertEx.NotNull(await store.GetRunAsync(cell[0])), score: 70));

        var page = await store.ListCellsAsync(project.Id);

        var only = page.Cells.Single();
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewPending, only.RankExclusionReason, "Every item answered: the inbox closes this cell, a re-run would not.");
        AssertEx.Null(only.Quality);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewPending, only.Items.Single(item => item.RunId == cell[1]).RankExclusionReason);
        AssertEx.Equal<int?>(70, only.Items.Single(item => item.RunId == cell[0]).QualityScore, "The approved item keeps its own score.");
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).TotalScored, "A cell waiting on a review is not yet scored.");
    }

    [Test]
    public async Task Cells_WhenOnlyGapIsATruncatedPendingReview_ReportsReviewPending()
    {
        // An approval's user score outranks truncation, so the review still closes this cell; a re-run is not what it needs.
        await using var context = await CreateDatabaseAsync("cell-review-truncated.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2, requiresReview: true);
        var cell = await SucceededCellAsync(store, project.Id, "stop", "length");
        _ = await store.SubmitReviewAsync(Approve(AssertEx.NotNull(await store.GetRunAsync(cell[0])), score: 70));

        var page = await store.ListCellsAsync(project.Id);

        var only = page.Cells.Single();
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonTruncated, only.Items.Single(item => item.RunId == cell[1]).RankExclusionReason,
            "The run keeps its own, more specific reason.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewPending, only.RankExclusionReason);
    }

    [Test]
    public async Task Cells_WhenAnItemIsMissingAndAReviewPending_ReportItemIncomplete()
    {
        await using var context = await CreateDatabaseAsync("cell-review-missing.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 2, requiresReview: true);
        var first = (await store.ListTaskItemsAsync(project.Id)).Single(static item => item.Index == 0);
        var run = await store.StartRunAsync(NewRun(project) with
        {
            TaskItemId = first.Id,
            TaskItemIndex = first.Index,
            CellKey = "cell:" + Guid.NewGuid().ToString("D") + ":1",
            TaskInputHash = first.InputHash,
            TaskItemSetHash = project.TaskItemSetHash
        });
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, claimed.Run.Version) with { PrimaryStopReason = "stop" });

        var page = await store.ListCellsAsync(project.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemIncomplete, page.Cells.Single().RankExclusionReason,
            "A review cannot supply an answer that was never produced.");
    }

    [Test]
    public async Task Cohort_WhenReviewProject_TotalScoredIgnoresJudgeScores()
    {
        await using var context = await CreateDatabaseAsync("cell-review-cohort.sqlite");
        var store = new BenchmarkStore(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);
        var project = await CreateSuiteAsync(store, itemCount: 1, requiresReview: true);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, Encoding.UTF8.GetBytes("""{"rubric":"a"}"""), new string('1', count: 64));
        _ = await JudgedSingleItemCellAsync(store, project.Id, activation.Revision.Id, judgeScore: 90);
        var approved = await JudgedSingleItemCellAsync(store, project.Id, activation.Revision.Id, judgeScore: 80);
        _ = await store.SubmitReviewAsync(Approve(approved, score: 50) with { PolicyRevisionId = activation.Revision.Id });

        var page = await store.ListCellsAsync(project.Id);

        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort.TotalScored, "Only the reviewed cell counts: a judge score alone is not a score in a review project.");
        AssertEx.Equal<int?>(50, page.Cells.Single(cell => cell.Items.Single().RunId == approved.Id).Quality);
    }

    /// <summary>One freeze of the whole suite into one cell, every primary succeeded with the stop reason at its item's position and nothing scored.</summary>
    private static async Task<IReadOnlyList<Guid>> SucceededCellAsync(BenchmarkStore store, Guid projectId, params string[] stopReasons)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var items = await store.ListTaskItemsAsync(projectId);
        var key = "cell:" + Guid.NewGuid().ToString("D") + ":1";
        var runs = await store.StartRunsAsync([
            .. items.Select(item => NewRun(project) with
            {
                TaskItemId = item.Id,
                TaskItemIndex = item.Index,
                CellKey = key,
                TaskInputHash = item.InputHash,
                TaskItemSetHash = project.TaskItemSetHash
            })
        ], project.Version);
        for (var index = 0; index < runs.Count; index++)
        {
            var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
            AssertEx.Equal(runs[index].Id, claimed.RunId, "The queue is FIFO by insert order.");
            _ = await store.MarkPrimarySucceededAsync(PrimarySuccess(claimed.RunId, claimed.Run.Version) with { PrimaryStopReason = stopReasons[index] });
        }

        return [.. runs.Select(static run => run.Id)];
    }

    private static BenchmarkReviewCommand Approve(BenchmarkRunRecord run, int score) =>
        new()
        {
            RunId = run.Id,
            ExpectedRunVersion = run.Version,
            State = BenchmarkReviewStates.Approved,
            Score = score
        };

    /// <summary>One single-item cell, judged to <paramref name="judgeScore" /> in the live cohort and not reviewed.</summary>
    private static async Task<BenchmarkRunRecord> JudgedSingleItemCellAsync(BenchmarkStore store, Guid projectId, Guid revisionId, int judgeScore)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var item = (await store.ListTaskItemsAsync(projectId)).Single();
        var run = await store.StartRunAsync(NewRun(project) with
        {
            TaskItemId = item.Id,
            TaskItemIndex = item.Index,
            CellKey = "cell:" + Guid.NewGuid().ToString("D") + ":1",
            TaskInputHash = item.InputHash,
            TaskItemSetHash = project.TaskItemSetHash
        });
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, primary.Run.Version) with
        {
            PrimaryStopReason = "stop",
            JudgeAttempt = new BenchmarkJudgeAttemptSeed
            {
                ExpectedJudgePolicyRevisionId = revisionId,
                RuntimeJson = Encoding.UTF8.GetBytes("""{"judgeRuntime":1}""")
            }
        });
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand
        {
            RunId = run.Id,
            ExpectedWorkVersion = judge.Version,
            JudgeResultJson = Encoding.UTF8.GetBytes("{}"),
            LastStreamSequence = 5,
            Score = judgeScore,
            VerifiedExecutionKey = "verified"
        });
    }

    private static async Task<BenchmarkProjectRecord> CreateSuiteAsync(BenchmarkStore store, int itemCount, bool requiresReview) =>
        await store.CreateProjectAsync(NewProject() with { RequiresReview = requiresReview },
            initialItems: [.. Enumerable.Range(0, itemCount).Select(index => Prompt("question " + index))]);

    private static async Task<BenchmarkProjectRecord> CreateSuiteAsync(BenchmarkStore store, int itemCount) =>
        await store.CreateProjectAsync(NewProject(),
            initialItems: [.. Enumerable.Range(0, itemCount).Select(index => Prompt("question " + index))]);

    private static Task<IReadOnlyList<Guid>> ScoredCellAsync(BenchmarkStore store, Guid projectId, params int?[] scores) =>
        VariantCellAsync(store, projectId, agentDefinitionId: null, agentName: "Agent", scores);

    /// <summary>A scored cell frozen against <paramref name="agentDefinitionId" />, or the project's agent when null.</summary>
    private static Task<IReadOnlyList<Guid>> VariantCellAsync(BenchmarkStore store, Guid projectId, Guid? agentDefinitionId, string agentName, params int?[] scores) =>
        ScoredCellAsync(store, projectId, [.. scores.Select(static score => (score, "stop"))], agentDefinitionId: agentDefinitionId, agentName: agentName);

    /// <summary>
    ///     One freeze of the whole suite into one cell, drained and scored in insert order. The stamps are exactly the
    ///     four the freeze service writes.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> ScoredCellAsync(BenchmarkStore store,
        Guid projectId,
        IReadOnlyList<(int? Score, string StopReason)> outcomes,
        string? cellKey = null,
        bool warmup = false,
        int? itemLimit = null,
        Guid? agentDefinitionId = null,
        string agentName = "Agent")
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var items = await store.ListTaskItemsAsync(projectId);
        var targets = items.Take(itemLimit ?? items.Count).ToArray();
        var key = cellKey ?? "cell:" + Guid.NewGuid().ToString("D") + ":1";
        var runs = await store.StartRunsAsync([
            .. targets.Select(item => NewRun(project) with
            {
                TaskItemId = item.Id,
                TaskItemIndex = item.Index,
                CellKey = key,
                TaskInputHash = item.InputHash,
                TaskItemSetHash = project.TaskItemSetHash,
                IsWarmup = warmup,
                RepeatGroupId = warmup ? Guid.NewGuid() : null,
                RepeatIndex = warmup ? 0 : null,
                AgentDefinitionId = agentDefinitionId,
                AgentName = agentName
            })
        ], project.Version);

        var ids = new List<Guid>(runs.Count);
        for (var index = 0; index < runs.Count; index++)
        {
            var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
            AssertEx.Equal(runs[index].Id, claimed.RunId, "The queue is FIFO by insert order.");
            var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(claimed.RunId, claimed.Run.Version) with
            {
                PrimaryStopReason = outcomes[index].StopReason
            });
            if (outcomes[index].Score is { } score)
            {
                _ = await store.SetUserScoreAsync(claimed.RunId, score, succeeded.Version);
            }

            ids.Add(claimed.RunId);
        }

        return ids;
    }

    private static async Task<Guid> LegacyScoredRunAsync(BenchmarkStore store, Guid projectId, int score)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var run = await store.StartRunAsync(NewRun(project));
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, claimed.Run.Version) with
        {
            PrimaryStopReason = "stop"
        });
        _ = await store.SetUserScoreAsync(run.Id, score, succeeded.Version);
        return run.Id;
    }

    private static BenchmarkTaskItemInput Prompt(string prompt) =>
        new()
        {
            PromptJson = JsonSerializer.SerializeToUtf8Bytes(prompt)
        };

    private static BenchmarkPrimarySuccessCommand PrimarySuccess(Guid runId, long expectedWorkVersion) =>
        new()
        {
            RunId = runId,
            ExpectedWorkVersion = expectedWorkVersion,
            OutputPartsJson = Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""),
            LastStreamSequence = 1,
            EffectiveContextTokens = 4096,
            DurationMs = 10,
            TotalTokens = 12,
            TokensPerSecond = 120
        };

    private static BenchmarkProjectInput NewProject() =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Benchmark",
            CoreTaskJson = JsonSerializer.SerializeToUtf8Bytes("answer the question"),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid()
        };

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new()
        {
            RunId = Guid.NewGuid(),
            ProjectId = project.Id,
            ExpectedProjectVersion = project.Version,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            PrimaryModelName = "model.gguf",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:" + new string('a', count: 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096
        };

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }
}
