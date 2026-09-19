namespace XE_Local_AI_Engine.Tests.GraphWorkflows.Import;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The post-migration write half of the one-shot Open Canvas import against a real database: turning each decrypted
///     canvas into a Graph Workflow definition.
///     <para>
///         The pre-migration read half is in <c>CanvasWorkflowImportReadTests</c> over in the persistence project,
///         because the table it reads no longer exists at head: seeding it means migrating to the migration before the
///         drop and writing raw rows, which is that project's migration-probe seam rather than this one's host.
///     </para>
///     <para>
///         Every test builds a host of its own, so an absolute count here is never a sibling's row.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CanvasWorkflowImportTests
{
    /// <summary>A fresh install has nothing to import and must say nothing about it.</summary>
    [Test]
    public async Task ImportAsync_WithAnEmptySnapshot_WritesNothingAndAnnouncesNothing()
    {
        await using var factory = NewHost();
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot { Candidates = [], FailedCount = 0 }, logger);

        AssertEx.Empty(await ListDefinitionsAsync(factory));
        AssertEx.Empty(logger.Entries, "a fresh install runs this on every first start; a summary there would be noise forever.");
    }

    /// <summary>
    ///     The happy path goes through the definition service, so a clean import inherits exactly the validation, the
    ///     hash and the node count the save endpoint writes.
    /// </summary>
    [Test]
    public async Task ImportAsync_WithACanvasTheValidatorAccepts_WritesOneDefinitionThatReadsBack()
    {
        await using var factory = NewHost();
        var candidate = new CanvasWorkflowImportCandidate { Id = Guid.NewGuid(), Name = "Release notes", GraphJson = CanvasGraphs.Linear, CreatedAtUtc = 1 };
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot { Candidates = [candidate], FailedCount = 0 }, logger);

        var summaries = await ListDefinitionsAsync(factory);
        var summary = summaries.Single();
        AssertEx.Equal("Release notes", summary.Name);
        AssertEx.Equal($"Imported from Open Canvas (canvas workflow {candidate.Id}).", summary.Description,
            "provenance without a schema change: the description is where an imported definition says where it came from.");
        AssertEx.Equal(expected: 3, summary.NodeCount);

        await using var scope = factory.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(summary.Id);
        AssertEx.Equal(expected: 3, GraphWorkflowGraphContract.ValidateAndCountNodes(definition.GraphJson, maxNodes: 200),
            "what round-trips out of the store is a definition a run could start.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "1 imported, 0 need attention, 0 failed"));
    }

    /// <summary>
    ///     The deliberate fallback. Preserving the row beats enforcing validity on data that is about to be deleted
    ///     either way, so a graph the service refuses is stored through the store instead — tagged so the definition
    ///     list says out loud that it will not run.
    /// </summary>
    [Test]
    public async Task ImportAsync_WithACanvasTheValidatorRefuses_StillStoresItTaggedForAttention()
    {
        await using var factory = NewHost();
        var candidate = new CanvasWorkflowImportCandidate { Id = Guid.NewGuid(), Name = "Odd one", GraphJson = CanvasGraphs.UnknownKind, CreatedAtUtc = 1 };
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot { Candidates = [candidate], FailedCount = 0 }, logger);

        var summary = (await ListDefinitionsAsync(factory)).Single();
        AssertEx.True(summary.Description?.StartsWith("IMPORT NEEDS ATTENTION: ", StringComparison.Ordinal) is true,
            $"the description an operator greps for is missing: '{summary.Description}'.");
        AssertEx.Contains(summary.Description, $"canvas workflow {candidate.Id}", message: "and it still says where the row came from.");
        AssertEx.Equal(expected: 3, summary.NodeCount, "the count is what the mapper wrote, because there is no parse to take it from.");

        await using var scope = factory.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(summary.Id);
        AssertEx.Equal(GraphHash(definition.GraphJson), definition.GraphHash, "the store hashed it exactly as it hashes a clean save.");
        _ = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraphContract.ValidateAndCountNodes(definition.GraphJson, maxNodes: 200),
            "which is the whole point: it is preserved, and it cannot run until an operator edits it.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "0 imported, 1 need attention, 0 failed"));
    }

    /// <summary>
    ///     One canvas that will not validate must never cost the operator the ones on either side of it, and the
    ///     summary an operator reads has to add up.
    /// </summary>
    [Test]
    public async Task ImportAsync_WithOneRefusedCanvasAmongThree_ImportsTheOtherTwoAndSummarizesAllOfIt()
    {
        await using var factory = NewHost();
        CanvasWorkflowImportCandidate[] candidates =
        [
            new() { Id = Guid.NewGuid(), Name = "First", GraphJson = CanvasGraphs.Linear, CreatedAtUtc = 1 },
            new() { Id = Guid.NewGuid(), Name = "Odd one", GraphJson = CanvasGraphs.UnknownKind, CreatedAtUtc = 2 },
            new() { Id = Guid.NewGuid(), Name = "Third", GraphJson = CanvasGraphs.WithPause, CreatedAtUtc = 3 }
        ];
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot { Candidates = candidates, FailedCount = 1 }, logger);

        var summaries = await ListDefinitionsAsync(factory);
        AssertEx.Equal("First, Odd one, Third", string.Join(", ", summaries.Select(static summary => summary.Name).Order(StringComparer.Ordinal)),
            "nothing is dropped for being invalid — the row travels and the operator decides.");
        AssertEx.Equal(expected: 1, summaries.Count(static summary => summary.Description?.StartsWith("IMPORT NEEDS ATTENTION", StringComparison.Ordinal) is true));

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Open Canvas one-shot import complete: 2 imported, 1 need attention, 1 failed."),
            "the failed count carries the reader's unreadable rows through into the one line an operator must not miss.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Open Canvas has been removed; canvas_workflows is dropped."));
    }

    /// <summary>
    ///     The import is outside the feature flag on purpose: an operator who never turns Graph Workflows on must not
    ///     silently lose their canvases, so it runs whatever <c>GraphWorkflows:Enabled</c> says.
    /// </summary>
    [Test]
    public async Task ImportAsync_WithGraphWorkflowsDisabled_StillImports()
    {
        // A host of its own for the one option this test is about.
        await using var factory = GraphWorkflowHostFixture.NewFactory(("GraphWorkflows:Enabled", "false"));
        var candidate = new CanvasWorkflowImportCandidate { Id = Guid.NewGuid(), Name = "Imported while off", GraphJson = CanvasGraphs.Linear, CreatedAtUtc = 1 };

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot { Candidates = [candidate], FailedCount = 0 }, new RecordingLogger<CanvasWorkflowImportTests>());

        AssertEx.Equal("Imported while off", (await ListDefinitionsAsync(factory)).Single().Name);
    }

    /// <summary>A host of this test's own, because every definition count here is an absolute one.</summary>
    private static TestServerWebAppFactory NewHost() =>
        GraphWorkflowHostFixture.NewFactory();

    private static async Task ImportAsync(TestServerWebAppFactory factory, CanvasWorkflowImportSnapshot snapshot, ILogger logger)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await CanvasWorkflowImport.ImportAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>(),
                                      scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(),
                                      snapshot,
                                      logger);
    }

    private static async Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListDefinitionsAsync();
    }

    /// <summary>The hash the store writes beside every graph, spelled out here so the column is pinned to the blob.</summary>
    private static string GraphHash(string graphJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(graphJson)));
}
