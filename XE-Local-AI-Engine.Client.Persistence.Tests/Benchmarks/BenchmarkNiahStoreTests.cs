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
