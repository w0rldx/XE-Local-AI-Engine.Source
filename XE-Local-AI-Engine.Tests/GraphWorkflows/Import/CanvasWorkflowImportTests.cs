namespace XE_Local_AI_Engine.Tests.GraphWorkflows.Import;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Both halves of the one-shot Open Canvas import against a real database: the pre-migration read that decrypts
///     <c>canvas_workflows</c> by hand, and the post-migration write that turns each canvas into a definition.
///     <para>
///         Every test builds a host of its own. The read is deliberately unfiltered — it takes EVERY row, because a cap
///         plus an unconditional drop migration destroys everything past the cap as its normal outcome — so a shared
///         database would let a sibling's rows into this one's count, and the count is the assertion that catches a
///         column silently failing to bind.
///     </para>
/// </summary>
public sealed class CanvasWorkflowImportTests
{
    /// <summary>
    ///     The encrypted-fixture case. Rows are seeded through the real store so the real save interceptor encrypts
    ///     them under AAD <c>graph_json</c>, then read back through the raw aliased-column path — which is the only
    ///     thing that proves the aliases bind. A count that came back short would mean a column reached its property as
    ///     a default and the drop migration was about to run anyway.
    /// </summary>
    [Test]
    public async Task ReadAsync_OverEncryptedRows_AnswersEveryCanvasInPlaintext()
    {
        await using var factory = NewHost();
        var first = await SeedCanvasAsync(factory, "Release notes", CanvasGraphs.Linear).ConfigureAwait(false);
        var second = await SeedCanvasAsync(factory, "Sign off", CanvasGraphs.WithPause).ConfigureAwait(false);

        var (snapshot, logger) = await ReadAsync(factory).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, snapshot.Candidates.Count, "the candidate count is what catches a column that silently failed to bind.");
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
        AssertEx.Equal("Release notes, Sign off", string.Join(", ", snapshot.Candidates.Select(static candidate => candidate.Name)),
            "ordered by creation, so ids and ordering stay natural.");
        AssertEx.Equal(first, snapshot.Candidates[0].Id);
        AssertEx.Equal(second, snapshot.Candidates[1].Id);
        AssertEx.Contains(snapshot.Candidates[0].GraphJson, "Summarize it.", message: "the graph comes back decrypted, not as the stored ciphertext.");
        AssertEx.True(logger.HasEntry(LogLevel.Information, "2 saved workflow(s) read before migrations"));
    }

    /// <summary>
    ///     The idempotency mechanism, stated as a test: on every start after the first the table is gone, so the read
    ///     is a permanent no-op. No marker table, no flag column, nothing to keep honest.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithNoCanvasWorkflowsTable_AnswersAnEmptySnapshotWithoutThrowing()
    {
        await using var factory = NewHost();
        _ = await SeedCanvasAsync(factory, "Gone after the drop", CanvasGraphs.Linear).ConfigureAwait(false);
        await ExecuteAsync(factory, "DROP TABLE canvas_workflows").ConfigureAwait(false);

        var (snapshot, _) = await ReadAsync(factory).ConfigureAwait(false);

        AssertEx.Empty(snapshot.Candidates);
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
    }

    /// <summary>
    ///     A blob that will not decrypt is the one thing this import genuinely loses, and it should be impossible: a
    ///     row written by the canvas endpoint decrypts under its own AAD. So it is counted and its id is named at
    ///     Error — the operator's only route back to it is the pre-migration snapshot — and the read carries on.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithARowThatWillNotDecrypt_CountsItAndStillReturnsTheOthers()
    {
        await using var factory = NewHost();
        var damaged = await SeedCanvasAsync(factory, "Damaged", CanvasGraphs.Linear).ConfigureAwait(false);
        _ = await SeedCanvasAsync(factory, "Intact", CanvasGraphs.WithPause).ConfigureAwait(false);
        await ExecuteAsync(factory, "UPDATE canvas_workflows SET graph_json = {0} WHERE id = {1}", new byte[] { 0x00, 0x01, 0x02 }, damaged).ConfigureAwait(false);

        var (snapshot, logger) = await ReadAsync(factory).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, snapshot.FailedCount);
        AssertEx.Equal("Intact", string.Join(", ", snapshot.Candidates.Select(static candidate => candidate.Name)), "one damaged row never costs the operator the rest.");
        AssertEx.True(logger.HasEntry(LogLevel.Error, damaged.ToString()), "the id is named, because it is what an operator pulls from the backup.");
    }

    /// <summary>
    ///     No cap and no option. A limit here would destroy every canvas past it as the import's NORMAL outcome, since
    ///     the drop migration runs in the same build whether the read took the row or not.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithMoreRowsThanAnyReasonableCap_ReadsEveryOne()
    {
        await using var factory = NewHost();
        for (var index = 0; index < 60; index++)
        {
            _ = await SeedCanvasAsync(factory, $"Canvas {index}", CanvasGraphs.Linear).ConfigureAwait(false);
        }

        var (snapshot, _) = await ReadAsync(factory).ConfigureAwait(false);

        AssertEx.Equal(expected: 60, snapshot.Candidates.Count);
    }

    /// <summary>A fresh install has nothing to import and must say nothing about it.</summary>
    [Test]
    public async Task ImportAsync_WithAnEmptySnapshot_WritesNothingAndAnnouncesNothing()
    {
        await using var factory = NewHost();
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot([], FailedCount: 0), logger).ConfigureAwait(false);

        AssertEx.Empty(await ListDefinitionsAsync(factory).ConfigureAwait(false));
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
        var candidate = new CanvasWorkflowImportCandidate(Guid.NewGuid(), "Release notes", CanvasGraphs.Linear, CreatedAtUtc: 1);
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot([candidate], FailedCount: 0), logger).ConfigureAwait(false);

        var summaries = await ListDefinitionsAsync(factory).ConfigureAwait(false);
        var summary = summaries.Single();
        AssertEx.Equal("Release notes", summary.Name);
        AssertEx.Equal($"Imported from Open Canvas (canvas workflow {candidate.Id}).", summary.Description,
            "provenance without a schema change: the description is where an imported definition says where it came from.");
        AssertEx.Equal(expected: 3, summary.NodeCount);

        await using var scope = factory.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(summary.Id).ConfigureAwait(false);
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
        var candidate = new CanvasWorkflowImportCandidate(Guid.NewGuid(), "Odd one", CanvasGraphs.UnknownKind, CreatedAtUtc: 1);
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot([candidate], FailedCount: 0), logger).ConfigureAwait(false);

        var summary = (await ListDefinitionsAsync(factory).ConfigureAwait(false)).Single();
        AssertEx.True(summary.Description?.StartsWith("IMPORT NEEDS ATTENTION: ", StringComparison.Ordinal) is true,
            $"the description an operator greps for is missing: '{summary.Description}'.");
        AssertEx.Contains(summary.Description, $"canvas workflow {candidate.Id}", message: "and it still says where the row came from.");
        AssertEx.Equal(expected: 3, summary.NodeCount, "the count is what the mapper wrote, because there is no parse to take it from.");

        await using var scope = factory.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(summary.Id).ConfigureAwait(false);
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
            new(Guid.NewGuid(), "First", CanvasGraphs.Linear, CreatedAtUtc: 1),
            new(Guid.NewGuid(), "Odd one", CanvasGraphs.UnknownKind, CreatedAtUtc: 2),
            new(Guid.NewGuid(), "Third", CanvasGraphs.WithPause, CreatedAtUtc: 3)
        ];
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot(candidates, FailedCount: 1), logger).ConfigureAwait(false);

        var summaries = await ListDefinitionsAsync(factory).ConfigureAwait(false);
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
        var candidate = new CanvasWorkflowImportCandidate(Guid.NewGuid(), "Imported while off", CanvasGraphs.Linear, CreatedAtUtc: 1);

        await ImportAsync(factory, new CanvasWorkflowImportSnapshot([candidate], FailedCount: 0), new RecordingLogger<CanvasWorkflowImportTests>()).ConfigureAwait(false);

        AssertEx.Equal("Imported while off", (await ListDefinitionsAsync(factory).ConfigureAwait(false)).Single().Name);
    }

    /// <summary>
    ///     A host of this test's own, because <see cref="CanvasWorkflowImport.ReadAsync" /> reads EVERY row of a
    ///     database and every count here is an absolute one.
    /// </summary>
    private static TestServerWebAppFactory NewHost() =>
        GraphWorkflowHostFixture.NewFactory();

    private static async Task<(CanvasWorkflowImportSnapshot Snapshot, RecordingLogger<CanvasWorkflowImportTests> Logger)> ReadAsync(TestServerWebAppFactory factory)
    {
        var logger = new RecordingLogger<CanvasWorkflowImportTests>();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        return (await CanvasWorkflowImport.ReadAsync(dbContext, logger).ConfigureAwait(false), logger);
    }

    private static async Task ImportAsync(TestServerWebAppFactory factory, CanvasWorkflowImportSnapshot snapshot, ILogger logger)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await CanvasWorkflowImport.ImportAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>(),
                                      scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(),
                                      snapshot,
                                      logger)
                                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     Seeded through the real canvas store, so the row is encrypted by the real save interceptor under the AAD the
    ///     reader decrypts with. A hand-built ciphertext would prove only that the test and the reader agree.
    /// </summary>
    private static async Task<Guid> SeedCanvasAsync(TestServerWebAppFactory factory, string name, string graphJson)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICanvasWorkflowStore>();
        return (await store.AddAsync(new CanvasWorkflowInput(name, graphJson)).ConfigureAwait(false)).Id;
    }

    private static async Task ExecuteAsync(TestServerWebAppFactory factory, string sql, params object[] parameters)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        _ = await dbContext.Database.ExecuteSqlRawAsync(sql, parameters).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListDefinitionsAsync().ConfigureAwait(false);
    }

    /// <summary>The hash the store writes beside every graph, spelled out here so the column is pinned to the blob.</summary>
    private static string GraphHash(string graphJson) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(graphJson)));
}
