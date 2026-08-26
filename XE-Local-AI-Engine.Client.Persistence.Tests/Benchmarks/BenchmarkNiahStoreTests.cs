namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     A generator and its cases as ROWS. The point of expanding at write time is that a case is an ordinary task
///     item, so what has to hold here is that every mechanism already built for items reaches it: the leaf set, the
///     item-set hash, the cascade, and the reorder that must not disturb any of them.
/// </summary>
public sealed class BenchmarkNiahStoreTests : IDisposable
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

    /// <summary>
    ///     The generator and its cases land together, and the cases are what a freeze fans out over. A generator that
    ///     existed for even one commit without its cases would be a project promising questions it cannot ask.
    /// </summary>
    [Test]
    public async Task CreateTaskItem_WithChildren_WritesTheGeneratorAndItsCasesInOneTransaction()
    {
        var (context, store) = await CreateStoreAsync("niah-create.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var probeId = Guid.NewGuid();

        var probe = await store.CreateTaskItemAsync(project.Id,
                                   project.Version,
                                   Probe(probeId),
                                   [Case(probeId, "case-a"), Case(probeId, "case-b"), Case(probeId, "case-c")])
                               .ConfigureAwait(false);

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 5, items.Count, "One authored prompt, one generator, three cases.");
        AssertEx.Equal(expected: 4, items.Count(static item => item.IsLeaf), "The generator is NOT one of the leaves a freeze fans out over.");
        AssertEx.Equal(expected: 3, items.Count(item => item.ParentItemId == probe.Id));
        AssertEx.True(items.Where(item => item.ParentItemId == probe.Id).All(static item => item.Index > 0),
            "Cases take indices after their generator's; indices are never reused.");
        AssertEx.Equal(items.Count, items.Select(static item => item.InputHash).Distinct(StringComparer.Ordinal).Count(),
            "Every item, cases included, hashes to its own value — so a run of one case is never read as a run of another.");
    }

    /// <summary>
    ///     The cases are inside the project's item-set hash because they are leaves — which is the whole reason the
    ///     expansion produces items rather than something a freeze invents. Adding a probe changes what the project
    ///     asks, so it must reset the cohort exactly as adding an authored item does.
    /// </summary>
    [Test]
    public async Task CreateTaskItem_WithChildren_MovesTheItemSetHash()
    {
        var (context, store) = await CreateStoreAsync("niah-set-hash.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var before = AssertEx.NotNull(project.TaskItemSetHash);
        var probeId = Guid.NewGuid();

        _ = await store.CreateTaskItemAsync(project.Id, project.Version, Probe(probeId), [Case(probeId, "case-a")]).ConfigureAwait(false);

        var after = AssertEx.NotNull((await store.GetProjectAsync(project.Id).ConfigureAwait(false))!.TaskItemSetHash);
        AssertEx.True(!string.Equals(before, after, StringComparison.Ordinal), "A project that asks a new question is a project with a different score.");
    }

    /// <summary>
    ///     Re-expansion replaces, atomically. A case left describing parameters its generator no longer has is a probe
    ///     that measures something nobody configured — so the old rows go in the same transaction the new ones arrive.
    /// </summary>
    [Test]
    public async Task UpdateTaskItem_WithChildren_ReplacesTheOldCases()
    {
        var (context, store) = await CreateStoreAsync("niah-update.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var probeId = Guid.NewGuid();
        var probe = await store.CreateTaskItemAsync(project.Id, project.Version, Probe(probeId), [Case(probeId, "old-a"), Case(probeId, "old-b")])
                               .ConfigureAwait(false);

        _ = await store.UpdateTaskItemAsync(project.Id, probe.Id, probe.Version, Probe(probeId, "revised probe"), [Case(probeId, "new-a")])
                       .ConfigureAwait(false);

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        var cases = items.Where(item => item.ParentItemId == probe.Id).ToArray();
        AssertEx.Equal(expected: 1, cases.Length, "Two cases were replaced by one; nothing is left over.");
        AssertEx.Equal("new-a", Encoding.UTF8.GetString(cases[0].PromptJson.Span));
        AssertEx.True(items.All(item => !Encoding.UTF8.GetString(item.PromptJson.Span).StartsWith("old-", StringComparison.Ordinal)),
            "No case survives describing parameters its generator no longer has.");
    }

    /// <summary>
    ///     Foreign keys are off on this connection and no cascade fires, so the ordered delete IS the referential
    ///     integrity — and a test that deleted through the EF graph would false-pass.
    /// </summary>
    [Test]
    public async Task DeleteTaskItem_TakesTheGeneratorsCasesWithIt()
    {
        var (context, store) = await CreateStoreAsync("niah-delete.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var probeId = Guid.NewGuid();
        var probe = await store.CreateTaskItemAsync(project.Id, project.Version, Probe(probeId), [Case(probeId, "case-a"), Case(probeId, "case-b")])
                               .ConfigureAwait(false);

        await store.DeleteTaskItemAsync(project.Id, probe.Id, probe.Version).ConfigureAwait(false);

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, items.Count, "The generator and both of its cases are gone; the authored prompt remains.");
        AssertEx.Equal("authored", Encoding.UTF8.GetString(items[0].PromptJson.Span));
    }

    /// <summary>
    ///     A drag-and-drop must not unrank a completed suite, and a probe's cases do not change that: the set hash is
    ///     ordered by the items' immutable ids, so reordering moves nothing a run is compared against.
    /// </summary>
    [Test]
    public async Task ReorderTaskItems_WithCasesPresent_LeavesTheItemSetHashAlone()
    {
        var (context, store) = await CreateStoreAsync("niah-reorder.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var probeId = Guid.NewGuid();
        _ = await store.CreateTaskItemAsync(project.Id, project.Version, Probe(probeId), [Case(probeId, "case-a"), Case(probeId, "case-b")])
                       .ConfigureAwait(false);
        var before = AssertEx.NotNull((await store.GetProjectAsync(project.Id).ConfigureAwait(false))!.TaskItemSetHash);
        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);

        var reordered = await store.ReorderTaskItemsAsync(project.Id, [.. items.Select(static item => item.Id).Reverse()]).ConfigureAwait(false);

        AssertEx.Equal(expected: 4, reordered.Count);
        AssertEx.Equal(before, (await store.GetProjectAsync(project.Id).ConfigureAwait(false))!.TaskItemSetHash,
            "Reordering asks the same questions, so it must not unrank the answers to them.");
    }

    /// <summary>
    ///     A project whose every leaf is display-only — a pure recall probe — has nothing to rank, which is NOT the
    ///     same as a cell missing an item. Saying "incomplete" there sends the operator looking for a question that
    ///     was never asked; the runs still carry their own scores, and those are the recall axis.
    /// </summary>
    [Test]
    public async Task ACellOfOnlyDisplayOnlyLeaves_IsUnrankedAsNoScore_NotItemIncomplete()
    {
        var (context, store) = await CreateStoreAsync("niah-only-project.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("authored")]).ConfigureAwait(false);
        var probeId = Guid.NewGuid();
        _ = await store.CreateTaskItemAsync(project.Id, project.Version, Probe(probeId), [Case(probeId, "case-a"), Case(probeId, "case-b")])
                       .ConfigureAwait(false);

        // Leaving only the two cases behind: every remaining leaf is excluded from the mean.
        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        var authored = items.Single(static item => string.Equals(item.Kind, BenchmarkTaskItemKinds.Prompt, StringComparison.Ordinal));
        await store.DeleteTaskItemAsync(project.Id, authored.Id, authored.Version).ConfigureAwait(false);

        var runIds = await ScoredCellAsync(store, project.Id, 100, 0).ConfigureAwait(false);

        var page = await store.ListRunsAsync(project.Id, skip: 0, take: 50).ConfigureAwait(false);
        var byId = page.Items.ToDictionary(static run => run.Id);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonNoScore, byId[runIds[0]].Judge!.RankExclusionReason,
            "Nothing here is scored, and there is no missing item to re-run.");
        AssertEx.True(runIds.All(id => byId[id].Rank is null), "A project with no scorable leaf ranks nothing.");
        AssertEx.Equal<int?>(100, byId[runIds[0]].QualityScore, "Each case keeps its own recall score — that IS the axis.");
        AssertEx.Equal<int?>(0, byId[runIds[1]].QualityScore, "Including the case that missed the needle.");
        AssertEx.True(byId.Values.All(static run => run.CellQuality is null), "No mean exists for them to report.");
        AssertEx.Equal(expected: 0, AssertEx.NotNull(page.RankCohort).RankedCount);
    }

    /// <summary>One freeze of every leaf into one cell, drained in insert order and scored by the operator.</summary>
    private static async Task<IReadOnlyList<Guid>> ScoredCellAsync(BenchmarkStore store, Guid projectId, params int[] scores)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId).ConfigureAwait(false));
        var leaves = (await store.ListTaskItemsAsync(projectId).ConfigureAwait(false)).Where(static item => item.IsLeaf).ToArray();
        var key = "cell:" + Guid.NewGuid().ToString("D") + ":1";
        var runs = await store.StartRunsAsync([.. leaves.Select(item => NewRun(project) with
        {
            TaskItemId = item.Id,
            TaskItemIndex = item.Index,
            CellKey = key,
            TaskInputHash = item.InputHash,
            TaskItemSetHash = project.TaskItemSetHash
        })], project.Version).ConfigureAwait(false);

        var ids = new List<Guid>(runs.Count);
        for (var index = 0; index < runs.Count; index++)
        {
            var claimed = AssertEx.NotNull(await store.ClaimNextAsync().ConfigureAwait(false));
            var succeeded = await store.MarkPrimarySucceededAsync(
                new BenchmarkPrimarySuccessCommand(claimed.RunId, claimed.Run.Version,
                    Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""), 1, 4096, 10, 12, 120) with
                {
                    PrimaryStopReason = "stop"
                }).ConfigureAwait(false);
            _ = await store.SetUserScoreAsync(claimed.RunId, scores[index], succeeded.Version).ConfigureAwait(false);
            ids.Add(claimed.RunId);
        }

        return ids;
    }

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""), "model.gguf",
            LocalModelOrigin.Imported, "v1:" + new string('a', count: 64), "Agent", 1, 4096);

    private static BenchmarkTaskItemInput Item(string prompt) =>
        new(Encoding.UTF8.GetBytes(prompt));

    private static BenchmarkTaskItemInput Probe(Guid id, string prompt = "a long-context probe") =>
        new(Encoding.UTF8.GetBytes(prompt),
            BenchmarkTaskItemKinds.Niah,
            GeneratorConfigJson: Encoding.UTF8.GetBytes("""{"contextTokens":[2048]}"""),
            Id: id);

    private static BenchmarkTaskItemInput Case(Guid parentId, string prompt) =>
        new(Encoding.UTF8.GetBytes(prompt),
            BenchmarkTaskItemKinds.NiahCase,
            VerifierConfigJson: Encoding.UTF8.GetBytes("""{"recall":{"expected":"ABC123"}}"""),
            GeneratorConfigJson: Encoding.UTF8.GetBytes("""{"contextTokens":2048,"depthPercent":50}"""),
            ParentItemId: parentId,
            CountsTowardScore: false);

    private static BenchmarkProjectInput NewProject() =>
        new(Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("""{"task":"answer"}"""), 4096, Guid.NewGuid());

    private async Task<(NodeChatDbContext Context, BenchmarkStore Store)> CreateStoreAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return (context, new BenchmarkStore(context, TimeProvider.System));
    }
}
