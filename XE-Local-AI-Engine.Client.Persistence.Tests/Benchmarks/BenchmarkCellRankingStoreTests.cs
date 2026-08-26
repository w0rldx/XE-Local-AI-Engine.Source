namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Ranking over CELLS: one model, one KV type, one repeat of the whole task-item suite. A cell ranks only when
///     every scorable item in it produced a rankable score, and the two identity stamps a run carries decide whether
///     it still answers a question — and a suite — the project recognises.
/// </summary>
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
        await using var context = await CreateDatabaseAsync("cell-mean.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 3).ConfigureAwait(false);

        var cell = await ScoredCellAsync(store, project.Id, 90, 60, 30).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-incomplete.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 3).ConfigureAwait(false);

        var cell = await ScoredCellAsync(store, project.Id, [(90, "stop"), (60, "stop"), (null, "length")]).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-two-freezes.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 2).ConfigureAwait(false);

        var first = await ScoredCellAsync(store, project.Id, 90, 90).ConfigureAwait(false);
        var second = await ScoredCellAsync(store, project.Id, 10, 10).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(90, byId[first[0]].CellQuality);
        AssertEx.Equal<int?>(10, byId[second[0]].CellQuality, "Two freezes are two cells; averaging them would be a wrong number, silently.");
        AssertEx.Equal<int?>(1, byId[first[0]].Rank);
        AssertEx.Equal<int?>(2, byId[second[0]].Rank);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    [Test]
    public async Task WarmupRuns_AreNotGroupedIntoACell_NorCounted()
    {
        // A warm-up sits at repeat index 0, so it forms a cell that could only ever complete if every leaf item also
        // got a warm-up run — and it would then sit in the denominator forever.
        await using var context = await CreateDatabaseAsync("cell-warmup.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 2).ConfigureAwait(false);
        var warmupCell = "cell:" + Guid.NewGuid().ToString("D") + ":0";
        _ = await ScoredCellAsync(store, project.Id, [(50, "stop")], cellKey: warmupCell, warmup: true, itemLimit: 1).ConfigureAwait(false);

        var measured = await ScoredCellAsync(store, project.Id, 80, 80).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(1, byId[measured[0]].Rank, "The measured cell is complete and ranks.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
        AssertEx.Equal(expected: 1, page.RankCohort!.TotalScored, "The warm-up's one-item cell is not a permanently incomplete denominator entry.");
    }

    [Test]
    public async Task LegacyRunsWithNoTaskItem_RankExactlyAsBefore_EvenAfterItemZeroIsMaterialized()
    {
        // R2 for stored rows: a project frozen before task suites keeps ranking after something materializes its
        // item 0. The materialized item must not turn every historical singleton into an incomplete cell.
        await using var context = await CreateDatabaseAsync("cell-legacy.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var high = await LegacyScoredRunAsync(store, project.Id, 90).ConfigureAwait(false);
        var low = await LegacyScoredRunAsync(store, project.Id, 10).ConfigureAwait(false);

        _ = await store.GetOrCreateItemsAsync(project.Id).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-item-revised.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 2).ConfigureAwait(false);
        var cell = await ScoredCellAsync(store, project.Id, 90, 90).ConfigureAwait(false);
        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);

        _ = await store.UpdateTaskItemAsync(project.Id, items[0].Id, items[0].Version, Prompt("rewritten question")).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-truncation-override.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 2).ConfigureAwait(false);

        var cell = await ScoredCellAsync(store, project.Id, [(70, "length"), (90, "stop")]).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal<int?>(70, byId[cell[0]].QualityScore, "An operator who scored a truncated answer still overrules the machine.");
        AssertEx.Equal<int?>(80, byId[cell[0]].CellQuality);
        AssertEx.Equal<int?>(1, byId[cell[0]].Rank);
    }

    [Test]
    public async Task AddingAnItem_ExcludesEveryHistoricalCell_ItemSetRevised()
    {
        await using var context = await CreateDatabaseAsync("cell-item-added.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 2).ConfigureAwait(false);
        var cell = await ScoredCellAsync(store, project.Id, 90, 90).ConfigureAwait(false);
        var refreshed = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));

        _ = await store.CreateTaskItemAsync(project.Id, refreshed.Version, Prompt("a third question")).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-item-deleted.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 3).ConfigureAwait(false);
        var cell = await ScoredCellAsync(store, project.Id, [(90, "stop"), (90, "stop"), (null, "length")]).ConfigureAwait(false);
        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);

        await store.DeleteTaskItemAsync(project.Id, items[2].Id, items[2].Version).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
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
        await using var context = await CreateDatabaseAsync("cell-item-reordered.sqlite").ConfigureAwait(false);
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await CreateSuiteAsync(store, itemCount: 3).ConfigureAwait(false);
        var cell = await ScoredCellAsync(store, project.Id, 90, 60, 30).ConfigureAwait(false);
        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);

        _ = await store.ReorderTaskItemsAsync(project.Id, [items[2].Id, items[0].Id, items[1].Id]).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.True(cell.All(runId => byId[runId].Judge!.RankExclusionReason is null));
        AssertEx.True(cell.All(runId => byId[runId].Rank == 1));
        AssertEx.Equal<int?>(60, byId[cell[0]].CellQuality);
        AssertEx.Equal(expected: 1, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    private static async Task<BenchmarkProjectRecord> CreateSuiteAsync(BenchmarkStore store, int itemCount) =>
        await store.CreateProjectAsync(NewProject(),
                       initialItems: [.. Enumerable.Range(0, itemCount).Select(index => Prompt("question " + index))])
                   .ConfigureAwait(false);

    private static Task<IReadOnlyList<Guid>> ScoredCellAsync(BenchmarkStore store, Guid projectId, params int?[] scores) =>
        ScoredCellAsync(store, projectId, [.. scores.Select(static score => (score, "stop"))]);

    /// <summary>
    ///     One freeze of the whole suite into one cell, drained and scored in insert order. The stamps are exactly the
    ///     four the freeze service writes.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> ScoredCellAsync(BenchmarkStore store,
        Guid projectId,
        IReadOnlyList<(int? Score, string StopReason)> outcomes,
        string? cellKey = null,
        bool warmup = false,
        int? itemLimit = null)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false));
        var items = await store.ListTaskItemsAsync(projectId).ConfigureAwait(false);
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
                RepeatIndex = warmup ? 0 : null
            })
        ], project.Version).ConfigureAwait(false);

        var ids = new List<Guid>(runs.Count);
        for (var index = 0; index < runs.Count; index++)
        {
            var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
            AssertEx.Equal(runs[index].Id, claimed.RunId, "The queue is FIFO by insert order.");
            var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(claimed.RunId, claimed.Run.Version) with
            {
                PrimaryStopReason = outcomes[index].StopReason
            }).ConfigureAwait(false);
            if (outcomes[index].Score is { } score)
            {
                _ = await store.SetUserScoreAsync(claimed.RunId, score, succeeded.Version).ConfigureAwait(false);
            }

            ids.Add(claimed.RunId);
        }

        return ids;
    }

    private static async Task<Guid> LegacyScoredRunAsync(BenchmarkStore store, Guid projectId, int score)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false));
        var run = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
        var succeeded = await store.MarkPrimarySucceededAsync(PrimarySuccess(run.Id, claimed.Run.Version) with
        {
            PrimaryStopReason = "stop"
        }).ConfigureAwait(false);
        _ = await store.SetUserScoreAsync(run.Id, score, succeeded.Version).ConfigureAwait(false);
        return run.Id;
    }

    private static BenchmarkTaskItemInput Prompt(string prompt) =>
        new(JsonSerializer.SerializeToUtf8Bytes(prompt));

    private static BenchmarkPrimarySuccessCommand PrimarySuccess(Guid runId, long expectedWorkVersion) =>
        new(runId, expectedWorkVersion, Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""), 1, 4096, 10, 12, 120);

    private static BenchmarkProjectInput NewProject() =>
        new(Guid.NewGuid(), "Benchmark", JsonSerializer.SerializeToUtf8Bytes("answer the question"), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""), "model.gguf",
            LocalModelOrigin.Imported, "v1:" + new string('a', count: 64), "Agent", 1, 4096);

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }
}
