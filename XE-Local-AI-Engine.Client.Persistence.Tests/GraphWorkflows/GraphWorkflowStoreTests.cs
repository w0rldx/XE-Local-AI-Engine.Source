namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class GraphWorkflowStoreTests
{
    /// <summary>
    ///     The round trip goes through a FRESH context, so what it proves is the interceptor pair rather than the
    ///     change tracker handing back the plaintext it already held.
    /// </summary>
    [Test]
    public async Task CreateDefinition_RoundTripsThroughAFreshContext()
    {
        using var fixture = new GraphWorkflowTestFixture();
        Guid definitionId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = GraphWorkflowTestFixture.StoreFor(context);
            var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "Triage", GraphWorkflowTestFixture.SampleGraph, nodeCount: 2, "Reads the inbox")
                                                        .ConfigureAwait(false);
            definitionId = created.Id;

            AssertEx.Equal(expected: 1, created.Version, "A fresh definition starts at version 1.");
            AssertEx.Equal(expected: 64, created.GraphHash.Length, "The graph hash is SHA-256 as lowercase hex.");
        }

        await using (var readContext = fixture.CreateContext())
        {
            var store = GraphWorkflowTestFixture.StoreFor(readContext);
            var read = await store.GetDefinitionAsync(definitionId).ConfigureAwait(false);

            AssertEx.Equal("Triage", read.Name);
            AssertEx.Equal("Reads the inbox", read.Description);
            AssertEx.Equal(GraphWorkflowTestFixture.SampleGraph, read.GraphJson, "The graph must come back byte-identical through the encrypt/decrypt pair.");
            AssertEx.Equal(expected: 2, read.NodeCount);
            AssertEx.Equal(expected: 1, read.SchemaVersion);
        }
    }

    /// <summary>
    ///     The list's promise, proved rather than asserted: with the graph blob corrupted beyond authentication the
    ///     list still answers, and only the read that genuinely needs the graph fails.
    /// </summary>
    [Test]
    public async Task ListDefinitions_NeverLoadsTheGraphBlob()
    {
        using var fixture = new GraphWorkflowTestFixture();
        Guid definitionId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = GraphWorkflowTestFixture.StoreFor(context);
            definitionId = (await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "Triage").ConfigureAwait(false)).Id;
        }

        await fixture.RawExecuteAsync("UPDATE graph_workflow_definitions SET graph_json = zeroblob(64) WHERE id = $id;",
                         command => command.Parameters.AddWithValue("$id", definitionId))
                     .ConfigureAwait(false);

        await using var readContext = fixture.CreateContext();
        var readStore = GraphWorkflowTestFixture.StoreFor(readContext);

        var listed = await readStore.ListDefinitionsAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 1, listed.Count, "The list must answer without decrypting a graph.");
        AssertEx.Equal("Triage", listed[0].Name);
        AssertEx.Equal(expected: 2, listed[0].NodeCount, "The node count is a column, so the list reports it without a parse.");

        _ = AssertEx.Throws<CryptographicException>(() => readStore.GetDefinitionAsync(definitionId).GetAwaiter().GetResult(),
            "and the read that does need the graph must fail on the same row, or the list above proved nothing.");
    }

    [Test]
    public async Task UpdateDefinition_WithAStaleVersion_Conflicts()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        var updated = await store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id, created.Version, "Renamed")).ConfigureAwait(false);
        AssertEx.Equal(created.Version + 1, updated.Version, "An accepted edit bumps the version.");

        _ = await AssertEx.ThrowsAsync<GraphWorkflowDefinitionConflictException>(
                              () => store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id, created.Version, "Renamed again")),
                              "A writer holding the pre-edit version must lose.")
                          .ConfigureAwait(false);

        var stillThere = await store.GetDefinitionAsync(created.Id).ConfigureAwait(false);
        AssertEx.Equal("Renamed", stillThere.Name, "The refused edit must not have landed.");
    }

    [Test]
    public async Task UpdateDefinition_WithoutAGraph_LeavesTheStoredOneAlone()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        var renamed = await store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id, created.Version, "Renamed")).ConfigureAwait(false);

        AssertEx.Equal(created.GraphJson, renamed.GraphJson, "A rename must not rewrite the graph.");
        AssertEx.Equal(created.GraphHash, renamed.GraphHash, "and it must not rewrite the hash that names the graph.");
        AssertEx.Equal(created.NodeCount, renamed.NodeCount, "or the node count the list reports for it.");
    }

    [Test]
    public async Task DeleteDefinition_WhileARunPinsIt_ThrowsTheDefinitionConflict()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        _ = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.WaitingForApproval).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowDefinitionConflictException>(() => store.DeleteDefinitionAsync(definition.Id),
                              "A definition must not be deleted out from under the run still executing it.")
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("graph_workflow_definitions").ConfigureAwait(false), "The refused delete must not have landed.");
    }

    /// <summary>
    ///     The other side of the same check: a terminal run pinned its own copy of the graph at start, so the
    ///     definition row is free to go and the run's history is unaffected.
    /// </summary>
    [Test]
    public async Task DeleteDefinition_WithOnlyTerminalRuns_Removes()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        _ = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed).ConfigureAwait(false);
        _ = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Cancelled).ConfigureAwait(false);

        await store.DeleteDefinitionAsync(definition.Id).ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, await fixture.RawTableCountAsync("graph_workflow_definitions").ConfigureAwait(false), "The definition row must be gone.");
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("graph_workflow_runs").ConfigureAwait(false),
            "and its terminal runs must stand: each pinned its own graph, so the history survives the definition.");

        _ = await AssertEx.ThrowsAsync<GraphWorkflowNotFoundException>(() => store.GetDefinitionAsync(definition.Id),
                              "A deleted definition reads back as not found rather than as an empty row.")
                          .ConfigureAwait(false);
    }
}
