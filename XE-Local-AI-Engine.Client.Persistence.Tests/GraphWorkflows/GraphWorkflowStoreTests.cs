namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    ///     The other half of the concurrency story, and the half one context cannot show: two writers each read version
    ///     N, so the store's own pre-save version check passes for BOTH. Only the concurrency token on the row stops
    ///     the later one from overwriting the earlier without either caller ever learning of the other.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_WhenAnotherContextWroteFirst_ConflictsOnTheConcurrencyToken()
    {
        using var fixture = new GraphWorkflowTestFixture();
        Guid definitionId;
        int version;

        await using (var seedContext = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(GraphWorkflowTestFixture.StoreFor(seedContext)).ConfigureAwait(false);
            definitionId = created.Id;
            version = created.Version;
        }

        await using var winnerContext = fixture.CreateContext();
        await using var loserContext = fixture.CreateContext();
        var winner = GraphWorkflowTestFixture.StoreFor(winnerContext);
        var loser = GraphWorkflowTestFixture.StoreFor(loserContext);

        // Both TRACK the row at version N before either writes, which is what makes this a race rather than a stale
        // PUT. Tracked, not AsNoTracking: the store's own load then resolves to this instance through the identity map
        // and still sees version N after the winner has committed N+1 — exactly the state a second request holds.
        _ = await winnerContext.GraphWorkflowDefinitions.SingleAsync(entity => entity.Id == definitionId).ConfigureAwait(false);
        _ = await loserContext.GraphWorkflowDefinitions.SingleAsync(entity => entity.Id == definitionId).ConfigureAwait(false);

        _ = await winner.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(definitionId, version, "Winner")).ConfigureAwait(false);

        var rejection = await AssertEx.ThrowsAsync<GraphWorkflowDefinitionConflictException>(
                                          () => loser.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(definitionId, version, "Loser")),
                                          "The second writer still holds version N, so the row's token must refuse it.")
                                      .ConfigureAwait(false);
        AssertEx.True(rejection.Message.Contains("changed by another writer", StringComparison.Ordinal),
            $"and it must be the TOKEN that refused it — the pre-save version check passes here, because this writer's view still says N: {rejection.Message}");

        await using var readContext = fixture.CreateContext();
        var read = await GraphWorkflowTestFixture.StoreFor(readContext).GetDefinitionAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal("Winner", read.Name, "The winner's write must survive the loser's refusal.");
        AssertEx.Equal(version + 1, read.Version, "and the version must have moved exactly once.");
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

    /// <summary>
    ///     A graph and its node count travel together or not at all. Accepting the graph alone would rewrite the blob
    ///     and its hash while leaving the PREVIOUS graph's count beside them, and the definition list — which reports
    ///     that column precisely so it never has to decrypt a blob — would then report a number for a document that no
    ///     longer has it.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_WithAGraphButNoNodeCount_IsRefused()
    {
        const string ReplacementGraph =
            """{"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"work","kind":"Agent"},{"key":"done","kind":"End"}],"edges":[]}""";

        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<ArgumentException>(() => store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id,
                                            created.Version,
                                            GraphJson: ReplacementGraph)),
                                        "A graph without its node count must be refused rather than written.")
                                    .ConfigureAwait(false);

        AssertEx.Equal(nameof(ArgumentException), refusal.GetType().Name, "and refused as an argument fault, not as a conflict or a not-found.");

        var unchanged = await store.GetDefinitionAsync(created.Id).ConfigureAwait(false);
        AssertEx.Equal(created.GraphJson, unchanged.GraphJson, "The refused edit must not have rewritten the graph.");
        AssertEx.Equal(created.GraphHash, unchanged.GraphHash, "nor the hash that names it.");
        AssertEx.Equal(created.Version, unchanged.Version, "nor bumped the version.");

        // The same graph WITH its count is accepted, so the refusal above is about the missing count and not about the
        // graph being rejected for some other reason.
        var replaced = await store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id,
                                      created.Version,
                                      GraphJson: ReplacementGraph,
                                      NodeCount: 3))
                                  .ConfigureAwait(false);

        AssertEx.Equal(expected: 3, replaced.NodeCount);
        AssertEx.Equal(ReplacementGraph, replaced.GraphJson);
        AssertEx.Equal(expected: 1, replaced.SchemaVersion, "An unsent schema version keeps the stored one: this node has exactly one.");
    }

    /// <summary>
    ///     The mirror of the rule above, and the reason it is a rule rather than a courtesy: the node count is DERIVED
    ///     from the graph, so a count arriving without one would sit beside a document it was not taken from — and the
    ///     definition list, which reports that column precisely so it never has to decrypt a blob, would report it as
    ///     the truth about a graph it never opened.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_WithANodeCountButNoGraph_IsRefused()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var created = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<ArgumentException>(() => store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id,
                                            created.Version,
                                            NodeCount: 99)),
                                        "A node count without the graph it counts must be refused rather than written.")
                                    .ConfigureAwait(false);

        AssertEx.Equal(nameof(ArgumentException), refusal.GetType().Name, "and refused as an argument fault, not as a conflict or a not-found.");

        var unchanged = await store.GetDefinitionAsync(created.Id).ConfigureAwait(false);
        AssertEx.Equal(created.NodeCount, unchanged.NodeCount, "The refused edit must not have rewritten the count.");
        AssertEx.Equal(created.Version, unchanged.Version, "nor bumped the version.");

        // The negative control: the SAME edit with neither member is an ordinary rename, so the refusal above is about
        // the orphaned count and not about the command being rejected for some other reason.
        var renamed = await store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(created.Id, created.Version, Name: "Renamed")).ConfigureAwait(false);

        AssertEx.Equal("Renamed", renamed.Name);
        AssertEx.Equal(created.NodeCount, renamed.NodeCount, "and a rename leaves the count exactly where the graph put it.");
    }

    /// <summary>
    ///     The conflict the create path owns, and its negative control. Both halves matter: the unique index refusing a
    ///     duplicate id IS "already exists", and a write that failed for any other reason is NOT — answering 409 to a
    ///     node whose table is gone would hide the real fault behind a retryable-looking one.
    ///     <para>
    ///         The duplicate goes through a SECOND context on purpose. Re-adding the id to the context that already
    ///         tracks it never reaches SQLite at all: EF's identity map refuses it first, which proves nothing about
    ///         what the store does with a constraint violation.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CreateDefinition_MapsOnlyTheDuplicateIdToAConflict()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var definitionId = Guid.NewGuid();

        _ = await GraphWorkflowTestFixture.StoreFor(context)
                                          .CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(definitionId,
                                              "Triage",
                                              GraphWorkflowTestFixture.SampleGraph,
                                              NodeCount: 2))
                                          .ConfigureAwait(false);

        await using var second = fixture.CreateContext();
        var store = GraphWorkflowTestFixture.StoreFor(second);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowDefinitionConflictException>(() => store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(definitionId,
                                  "Triage again",
                                  GraphWorkflowTestFixture.SampleGraph,
                                  NodeCount: 2)),
                              "A second definition under one id is the unique index refusing it, which is a conflict.")
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("graph_workflow_definitions").ConfigureAwait(false), "and the refused row must not have landed.");

        // The negative control: with the table gone the write fails for a reason that is not a duplicate, and the
        // caller has to hear that rather than "it already exists".
        await fixture.RawExecuteAsync("DROP TABLE graph_workflow_definitions;").ConfigureAwait(false);

        var broken = await AssertEx.ThrowsAsync<DbUpdateException>(() => store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(),
                                           "Nowhere to go",
                                           GraphWorkflowTestFixture.SampleGraph,
                                           NodeCount: 2)),
                                       "A write that failed for anything but a unique violation must travel as itself.")
                                   .ConfigureAwait(false);

        _ = AssertEx.NotNull(broken.InnerException, "and it must still carry the SQLite fault that explains it.");
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
