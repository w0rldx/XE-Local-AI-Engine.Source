namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The task-item write surface: atomic creation with the project, the legacy-only item-0 backfill, the revision
///     and input-hash bump every mutation makes, and the item-set hash whose movement resets the rank cohort.
/// </summary>
public sealed class BenchmarkTaskItemStoreTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] PolicyBytes = Encoding.UTF8.GetBytes("""{"rubric":"v1"}""");
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
    ///     A project never exists without a question to ask, so no read path has to invent one. Both halves are in one
    ///     transaction with the judge activation, which is why the project's set hash is already populated on return.
    /// </summary>
    [Test]
    public async Task CreateProject_WithInitialItems_WritesProjectJudgeAndItemsInOneTransaction()
    {
        var (context, store) = await CreateStoreAsync("create-with-items.sqlite").ConfigureAwait(false);
        await using var scope = context;

        var project = await store.CreateProjectAsync(NewProject(),
                                     new BenchmarkJudgePolicyChangeInput(PolicyBytes, PolicyHash),
                                     [Item("first"), Item("second")])
                                 .ConfigureAwait(false);

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, items.Count);
        AssertEx.Equal(expected: 0, items[0].Index);
        AssertEx.Equal(expected: 1, items[1].Index);
        AssertEx.Equal("first", Encoding.UTF8.GetString(items[0].PromptJson.Span));
        AssertEx.Equal(expected: 1, items[0].Revision, "A freshly created item is revision 1.");
        AssertEx.True(items[0].InputHash.StartsWith("v1:", StringComparison.Ordinal), "The input hash is versioned so a future scheme is distinguishable.");
        AssertEx.True(!string.Equals(items[0].InputHash, items[1].InputHash, StringComparison.Ordinal), "Two different prompts hash differently.");
        AssertEx.NotNull(project.TaskItemSetHash, "A project created with items already knows what its whole question set is.");
        AssertEx.True(project.CurrentJudgePolicyRevisionId is not null, "The judge activation shares that transaction.");
    }

    /// <summary>The atomicity that matters: a rejected item must not leave a project behind for an operator to find.</summary>
    [Test]
    public async Task CreateProject_WhenAnItemIsRejected_RollsBackTheProject()
    {
        var (context, store) = await CreateStoreAsync("create-items-rollback.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var input = NewProject();

        // Two items claiming one id: the unique key rejects the second and the whole creation must go with it.
        var duplicate = Guid.NewGuid();
        _ = await AssertEx.ThrowsAsync<Exception>(() => store.CreateProjectAsync(input,
                                  judgePolicy: null,
                                  [Item("first") with { Id = duplicate }, Item("second") with { Id = duplicate }]),
                              "An item insert that cannot succeed must take the project creation down with it.")
                          .ConfigureAwait(false);

        context.ChangeTracker.Clear();
        AssertEx.Null(await store.GetProjectAsync(input.Id).ConfigureAwait(false), "No project may survive a failed item insert.");
    }

    /// <summary>
    ///     The surviving legacy path. A migration has no node encryption key, so item 0 of a project created before
    ///     task items existed is materialized here — inside the normal EF write path, where both interceptors run.
    /// </summary>
    [Test]
    public async Task GetOrCreateItems_OnALegacyProject_MaterializesItemZeroFromTheCoreTaskAndIsIdempotent()
    {
        var (context, store) = await CreateStoreAsync("legacy-backfill.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        AssertEx.Empty(await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false));

        var first = await store.GetOrCreateItemsAsync(project.Id).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, first.Count);
        AssertEx.Equal(expected: 0, first[0].Index);
        AssertEx.Equal(BenchmarkTaskItemKinds.Prompt, first[0].Kind);
        AssertEx.True(first[0].PromptJson.Span.SequenceEqual(project.CoreTaskJson.Span), "Item 0 asks exactly what the project's core task asked.");

        var second = await store.GetOrCreateItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, second.Count);
        AssertEx.Equal(first[0].Id, second[0].Id, "The backfill is idempotent — a second touch reads, it does not write.");

        // Materializing item 0 changes nothing about what the project asks, so the hash every historical run is
        // compared against must not move: doing so would unrank a whole project's history for a bookkeeping write.
        AssertEx.Null(AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false)).TaskItemSetHash,
            "The lazy backfill must leave the set hash null.");
    }

    /// <summary>
    ///     Adding and deleting change which questions the project asks and must move the set hash; reordering changes
    ///     no question and must not, because the hash is ordered by the items' immutable ids rather than their indices.
    ///     A cosmetic drag-and-drop unranking a completed suite is the bug this ordering exists to prevent.
    /// </summary>
    [Test]
    public async Task TaskItemSetHash_MovesOnAddAndDelete_AndIsUnchangedByAReorder()
    {
        var (context, store) = await CreateStoreAsync("set-hash.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("first"), Item("second")]).ConfigureAwait(false);
        var afterCreate = AssertEx.NotNull(project.TaskItemSetHash);

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        _ = await store.ReorderTaskItemsAsync(project.Id, [items[1].Id, items[0].Id]).ConfigureAwait(false);
        var reordered = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        AssertEx.Equal(afterCreate, reordered.TaskItemSetHash, "A reorder asks the same questions, so the set hash must not move.");
        var reorderedItems = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(items[1].Id, reorderedItems[0].Id, "The reorder still has to renumber the items.");
        AssertEx.Equal(expected: 1, reorderedItems[0].Revision, "A reorder is not an edit: no revision moves.");

        var added = await store.CreateTaskItemAsync(project.Id, reordered.Version, Item("third")).ConfigureAwait(false);
        var afterAdd = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        AssertEx.True(!string.Equals(afterCreate, afterAdd.TaskItemSetHash, StringComparison.Ordinal), "Adding a question changes what the project asks.");

        await store.DeleteTaskItemAsync(project.Id, added.Id, added.Version).ConfigureAwait(false);
        var afterDelete = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        AssertEx.Equal(afterCreate, afterDelete.TaskItemSetHash, "Deleting exactly what was added returns the project to the set it had.");
    }

    [Test]
    public async Task UpdateTaskItem_BumpsTheRevisionAndRecomputesTheInputHash()
    {
        var (context, store) = await CreateStoreAsync("update-item.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("first")]).ConfigureAwait(false);
        var item = (await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false))[0];

        var updated = await store.UpdateTaskItemAsync(project.Id, item.Id, item.Version, Item("first edited")).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, updated.Revision);
        AssertEx.True(!string.Equals(item.InputHash, updated.InputHash, StringComparison.Ordinal), "An edited item is a different question.");

        // A reference answer and a verifier override are inside the hash too: the run that answered the old instance
        // was graded against something else, whether or not the prompt itself moved.
        var withReference = await store.UpdateTaskItemAsync(project.Id,
                                           item.Id,
                                           updated.Version,
                                           Item("first edited") with { ReferenceAnswerJson = Encoding.UTF8.GetBytes("expected") })
                                       .ConfigureAwait(false);
        AssertEx.True(!string.Equals(updated.InputHash, withReference.InputHash, StringComparison.Ordinal),
            "A reference answer participates in the input hash.");

        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.UpdateTaskItemAsync(project.Id, item.Id, updated.Version, Item("stale")),
                              "The item's version is the write's compare-and-swap target.")
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(
                              () => store.UpdateTaskItemAsync(project.Id, item.Id, withReference.Version, Item("first edited") with { Kind = BenchmarkTaskItemKinds.Niah }),
                              "A kind change under a stable id is a different item wearing the old identity.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteTaskItem_RefusesTheLastLeaf_AndTakesAGeneratorsChildrenWithIt()
    {
        var (context, store) = await CreateStoreAsync("delete-item.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("only")]).ConfigureAwait(false);
        var only = (await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false))[0];

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.DeleteTaskItemAsync(project.Id, only.Id, only.Version),
                              "A benchmark project always asks at least one question.")
                          .ConfigureAwait(false);

        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        var generator = await store.CreateTaskItemAsync(project.Id, current.Version, Item("generator") with { Kind = BenchmarkTaskItemKinds.Niah }).ConfigureAwait(false);
        current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        _ = await store.CreateTaskItemAsync(project.Id,
                           current.Version,
                           Item("case") with { Kind = BenchmarkTaskItemKinds.NiahCase, ParentItemId = generator.Id, CountsTowardScore = false })
                       .ConfigureAwait(false);

        // Foreign keys are off on this connection, so the delete order IS the referential integrity — a child left
        // behind would point at a generator that no longer exists and nothing would complain.
        await store.DeleteTaskItemAsync(project.Id, generator.Id, generator.Version).ConfigureAwait(false);

        var remaining = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, remaining.Count, "The generator and its case go together.");
        AssertEx.Equal(only.Id, remaining[0].Id);
    }

    /// <summary>
    ///     The project score is a mean over the item set, so changing the set changes what the score means — the same
    ///     reset a judge-policy activation performs. A reorder is exempt for the same reason its hash is.
    /// </summary>
    [Test]
    public async Task ItemSetChange_ResetsTheRankCohort_AndAReorderDoesNot()
    {
        var (context, store) = await CreateStoreAsync("cohort-reset.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(),
                                     new BenchmarkJudgePolicyChangeInput(PolicyBytes, PolicyHash),
                                     [Item("first"), Item("second")])
                                 .ConfigureAwait(false);
        var generation = AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).CohortGeneration;

        var items = await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false);
        _ = await store.ReorderTaskItemsAsync(project.Id, [items[1].Id, items[0].Id]).ConfigureAwait(false);
        AssertEx.Equal(generation,
            AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).CohortGeneration,
            "A reorder must not reset a cohort: nothing about what was asked changed.");

        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        _ = await store.CreateTaskItemAsync(project.Id, current.Version, Item("third")).ConfigureAwait(false);
        AssertEx.Equal(generation + 1,
            AssertEx.NotNull(await store.GetCurrentJudgePolicyRevisionAsync(project.Id).ConfigureAwait(false)).CohortGeneration,
            "Adding an item resets the cohort, exactly as a policy activation does.");
    }

    /// <summary>
    ///     The primary guard. The ranking read's staleness exclusions are a safety net for completed history; refusing
    ///     the edit while work is live is what stops a run being frozen against one revision and judged against another.
    /// </summary>
    [Test]
    public async Task TaskItemWrites_AreRefusedWhileTheProjectHasLiveWork()
    {
        var (context, store) = await CreateStoreAsync("active-work.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("first")]).ConfigureAwait(false);
        var item = (await store.ListTaskItemsAsync(project.Id).ConfigureAwait(false))[0];
        var afterFreeze = await store.StartRunAsync(NewRun(project)).ConfigureAwait(false);

        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));
        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.CreateTaskItemAsync(project.Id, current.Version, Item("second")))
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.UpdateTaskItemAsync(project.Id, item.Id, item.Version, Item("edited")))
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.DeleteTaskItemAsync(project.Id, item.Id, item.Version)).ConfigureAwait(false);
        AssertEx.NotNull(afterFreeze, "The freeze is what made the project busy.");
    }

    /// <summary>
    ///     Until the freeze fans out over items, a run is still a cell of one — but the stamps are NOT NULL from this
    ///     migration on, so it carries a singleton cell key and the project's own set hash.
    /// </summary>
    [Test]
    public async Task StartRun_StampsASingletonCellAndTheProjectsSetHash()
    {
        var (context, store) = await CreateStoreAsync("run-stamps.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var withItems = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("first")]).ConfigureAwait(false);
        var run = await store.StartRunAsync(NewRun(withItems)).ConfigureAwait(false);

        var stored = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == run.Id).ConfigureAwait(false);
        AssertEx.Equal("cell:" + run.Id.ToString("D"), stored.CellKey, "A run that is a cell of one derives its key from its own id.");
        AssertEx.Equal(AssertEx.NotNull(withItems.TaskItemSetHash), stored.TaskItemSetHash);
        AssertEx.Equal("v1:legacy", stored.TaskInputHash, "The per-item stamp lands when the freeze fans out over items.");

        // A project with no items at all — the shape every project had before this migration — stamps the legacy
        // constant on both axes, which is what it is also compared against.
        var legacy = await store.CreateProjectAsync(NewProject()).ConfigureAwait(false);
        var legacyRun = await store.StartRunAsync(NewRun(legacy)).ConfigureAwait(false);
        var storedLegacy = await context.BenchmarkRuns.AsNoTracking().SingleAsync(entity => entity.Id == legacyRun.Id).ConfigureAwait(false);
        AssertEx.Equal("v1:legacy", storedLegacy.TaskItemSetHash);
        AssertEx.Equal("v1:legacy", storedLegacy.TaskInputHash);
    }

    [Test]
    public async Task TaskItemWrites_RejectAnEmptyPromptAndAnUnknownKind()
    {
        var (context, store) = await CreateStoreAsync("item-validation.sqlite").ConfigureAwait(false);
        await using var scope = context;
        var project = await store.CreateProjectAsync(NewProject(), judgePolicy: null, [Item("first")]).ConfigureAwait(false);
        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id).ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(
                              () => store.CreateTaskItemAsync(project.Id, current.Version, new BenchmarkTaskItemInput(ReadOnlyMemory<byte>.Empty)))
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.CreateTaskItemAsync(project.Id, current.Version, Item("x") with { Kind = "invented" }))
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(
                              () => store.CreateTaskItemAsync(project.Id, current.Version, Item("x") with { ParentItemId = Guid.NewGuid() }),
                              "A parent from another project is not a parent.")
                          .ConfigureAwait(false);
    }

    private static BenchmarkTaskItemInput Item(string prompt) =>
        new(Encoding.UTF8.GetBytes(prompt));

    private static BenchmarkProjectInput NewProject(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("""{"task":"answer"}"""), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand NewRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""), "model.gguf",
            LocalModelOrigin.Imported, "v1:" + new string('a', count: 64), "Agent", 1, 4096);

    private async Task<(NodeChatDbContext Context, BenchmarkStore Store)> CreateStoreAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return (context, new BenchmarkStore(context, TimeProvider.System));
    }
}
