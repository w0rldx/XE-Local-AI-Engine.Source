namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevWorkflowStoreTests
{
    /// <summary>T-1: the run's counter is the one watermark, and it never repeats or skips across child tables.</summary>
    [Test]
    public async Task Sequence_IsStrictlyIncreasingAndGapFreeAcrossEveryChildTable()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        var artifactId = Guid.NewGuid();
        var appended = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(seed.RunId,
                                      artifactId,
                                      nodeRunId,
                                      version,
                                      Guid.NewGuid(),
                                      DevWorkflowArtifactKind.Research,
                                      "brief",
                                      "text/markdown",
                                      "hash-1",
                                      SizeBytes: 12,
                                      "reference-1"))
                                  .ConfigureAwait(false);
        var transitioned = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                          nodeRunId,
                                          appended.Version,
                                          DevWorkflowNodeRunStatus.Queued,
                                          QueueReason: "awaiting-agent-slot"))
                                      .ConfigureAwait(false);

        var run = await store.GetRunAsync(seed.RunId).ConfigureAwait(false);
        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);

        AssertEx.Equal(transitioned.Sequence, run.LastSequence, "The run's watermark must be the last sequence any child write allocated.");

        // Gap-free: every value from 1 to the watermark is claimed exactly once, by an event, a node run or an artifact.
        var claimed = new List<long>();
        claimed.AddRange(events.Select(item => item.Sequence));
        claimed.AddRange((await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).Select(item => item.Sequence));
        claimed.AddRange((await store.ListArtifactsAsync(seed.RunId).ConfigureAwait(false)).Select(item => item.Sequence));
        claimed.Sort();

        AssertEx.Equal(run.LastSequence, claimed.Count, "Every allocated sequence must belong to exactly one row.");
        for (var index = 0; index < claimed.Count; index++)
        {
            AssertEx.Equal(index + 1L, claimed[index], "The run's sequence values must be strictly increasing and gap-free.");
        }
    }

    /// <summary>T-2: a stale expected version loses; the Any sentinel never does.</summary>
    [Test]
    public async Task ExpectedVersion_RejectsAStaleWriterAndTheAnySentinelAlwaysWins()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var moved = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, seed.RunVersion, DevWorkflowRunStatus.Running)).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, seed.RunVersion, DevWorkflowRunStatus.Paused)),
                              "A writer holding the pre-transition version must lose.")
                          .ConfigureAwait(false);

        var withSentinel = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, DevWorkflowVersions.Any, DevWorkflowRunStatus.Paused))
                                      .ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, withSentinel.Status);
        AssertEx.True(withSentinel.Version > moved.Version, "The sentinel write must still bump the version it did not check.");
    }

    /// <summary>T-3, sequential half: a replayed operation returns the recorded result and appends nothing.</summary>
    [Test]
    public async Task OperationId_ReplayReturnsTheRecordedResultWithoutAppending()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var operationId = Guid.NewGuid();
        var first = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(seed.RunId,
                                   seed.RunVersion,
                                   DevWorkflowEventTypes.PolicyResolved,
                                   OperationId: operationId))
                               .ConfigureAwait(false);
        var eventsAfterFirst = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);

        var replay = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(seed.RunId,
                                    first.Version,
                                    DevWorkflowEventTypes.PolicyResolved,
                                    OperationId: operationId))
                                .ConfigureAwait(false);
        var eventsAfterReplay = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);

        AssertEx.Equal(first.Sequence, replay.Sequence, "A replay must answer with the watermark the first attempt allocated.");
        AssertEx.Equal(eventsAfterFirst.Count, eventsAfterReplay.Count, "A replayed operation must not append a second event.");
    }

    /// <summary>
    ///     T-3, concurrent half: two writers on separate connections submitting the same operation id both receive the
    ///     one recorded result, and neither gets an exception. That is the contract an idempotency key exists to give —
    ///     a run is written by the dispatcher AND by human HTTP actions that can genuinely arrive together, unlike a
    ///     work session, which one supervisor drives.
    ///     <para>
    ///         What satisfies it here is the store's <em>in-transaction</em> query-first check, not its post-failure
    ///         recovery: EF opens SQLite transactions as <c>BEGIN IMMEDIATE</c>, so the loser blocks on the writer lock
    ///         until the winner commits and then sees the recorded operation before writing anything. Measured — the
    ///         recovery branch is never entered on this provider.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OperationId_ConcurrentWritersBothReceiveTheRecordedResult()
    {
        using var fixture = new DevWorkflowTestFixture();
        Guid runId;
        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = DevWorkflowTestFixture.StoreFor(context);
            runId = (await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false)).RunId;
        }

        var operationId = Guid.NewGuid();

        // Separate contexts: two writers on one DbContext would share a change tracker and never actually contend.
        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var firstStore = DevWorkflowTestFixture.StoreFor(firstContext);
        var secondStore = DevWorkflowTestFixture.StoreFor(secondContext);

        var command = new AppendDevWorkflowEventCommand(runId, DevWorkflowVersions.Any, DevWorkflowEventTypes.PolicyResolved, OperationId: operationId);
        var results = await Task.WhenAll(Task.Run(() => firstStore.AppendEventAsync(command)), Task.Run(() => secondStore.AppendEventAsync(command))).ConfigureAwait(false);

        AssertEx.Equal(results[0].Sequence, results[1].Sequence, "Both writers must be told about the one event that landed.");

        await using var readContext = fixture.CreateContext();
        var events = await DevWorkflowTestFixture.StoreFor(readContext).ListEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationId == operationId), "Exactly one event may carry the operation id.");
    }

    /// <summary>T-13: the partial unique index, not a check-then-insert, is what makes one live run per work item true.</summary>
    [Test]
    public async Task StartRun_RejectsASecondLiveRunAndAdmitsOneAfterTheFirstTerminates()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);
        var definition = await store.GetDefinitionAsync(seed.DefinitionId).ConfigureAwait(false);

        StartDevWorkflowRunCommand Second() =>
            new(Guid.NewGuid(), seed.WorkItemId, definition.Id, definition.Version, definition.GraphHash, DevWorkflowTestFixture.SampleGraph);

        _ = await AssertEx.ThrowsAsync<DevWorkflowRunInFlightException>(() => store.StartRunAsync(Second()),
                              "A second live run on one work item must be rejected by the database, not by a racy read-modify-write.")
                          .ConfigureAwait(false);

        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, DevWorkflowVersions.Any, DevWorkflowRunStatus.Completed)).ConfigureAwait(false);

        var next = await store.StartRunAsync(Second()).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Pending, next.Status, "Once the first run is terminal, the work item may start another.");
    }

    /// <summary>The list is two queries whatever the row count, and it carries each item's latest run and its counters.</summary>
    [Test]
    public async Task ListWorkItems_CarriesTheLatestRunAndItsNodeCounters()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store, "Ship the thing").ConfigureAwait(false);

        var gateNodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, Guid.NewGuid(), "research", seed.RunVersion).ConfigureAwait(false);
        version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, gateNodeRunId, "approval", version, DevWorkflowNodeType.HumanGate).ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           gateNodeRunId,
                           version,
                           DevWorkflowNodeRunStatus.WaitingForApproval,
                           PendingDecisionKind: DevWorkflowDecisionKind.Approve))
                       .ConfigureAwait(false);

        var listed = await store.ListWorkItemsAsync().ConfigureAwait(false);
        var item = listed.Single();

        AssertEx.Equal("Ship the thing", item.Title);

        // The list projects the entity rather than its columns, so this also pins that the materialization interceptor
        // still runs inside a projection — otherwise the request would come back as ciphertext, silently.
        AssertEx.Equal("Seeded request", item.Request);
        AssertEx.Equal(seed.RunId, item.LatestRunId);
        AssertEx.Equal("Seeded definition", item.LatestRunDefinitionName);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Active, item.Status, "Starting a run makes the work item active, and the runtime is what writes that.");
        AssertEx.Equal(expected: 2, item.LatestRunNodes.Total);
        AssertEx.Equal(expected: 1, item.LatestRunNodes.PendingDecisionCount);
        AssertEx.Equal(gateNodeRunId, item.LatestRunNodes.BlockingGateNodeRunId);

        var filteredOut = await store.ListWorkItemsAsync(DevWorkflowWorkItemStatus.Draft).ConfigureAwait(false);
        AssertEx.Empty(filteredOut, "The status filter belongs in the query, not in a post-filter the caller forgets.");
    }

    /// <summary>Archiving hides a definition from the picker and leaves the runs that pinned it alone.</summary>
    [Test]
    public async Task ArchiveDefinition_HidesItFromTheListWithoutTouchingItsRuns()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        _ = await store.ArchiveDefinitionAsync(seed.DefinitionId).ConfigureAwait(false);

        AssertEx.Empty(await store.ListDefinitionsAsync().ConfigureAwait(false), "An archived definition must not reach the picker.");
        AssertEx.Equal(expected: 1, (await store.ListDefinitionsAsync(includeArchived: true).ConfigureAwait(false)).Count);

        var run = await store.GetRunAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowTestFixture.SampleGraph, run.GraphJson, "A run renders from its own pinned snapshot, so archiving cannot disturb it.");
    }

    /// <summary>The definition list never decrypts a graph blob, which is what the denormalized node count is for.</summary>
    [Test]
    public async Task ListDefinitions_ReportsTheNodeCountWithoutTheGraph()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var created = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(),
                                     "Feature development",
                                     DevWorkflowTestFixture.SampleGraph,
                                     NodeCount: 7,
                                     DevWorkflowDefinitionSource.Seeded,
                                     "feature-development-v1"))
                                 .ConfigureAwait(false);

        var summary = (await store.ListDefinitionsAsync().ConfigureAwait(false)).Single();
        AssertEx.Equal(expected: 7, summary.NodeCount);
        AssertEx.Equal(created.GraphHash, summary.GraphHash, "The hash is written with the graph, so the summary can name the graph without loading it.");
        AssertEx.Equal("feature-development-v1", summary.SeedSlug);
    }

    /// <summary>Re-seeding under a slug already taken must fail on the filtered unique index, not duplicate the template.</summary>
    [Test]
    public async Task CreateDefinition_RejectsADuplicateSeedSlug()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);

        static CreateDevWorkflowDefinitionCommand Seeded() =>
            new(Guid.NewGuid(), "Research to approval", DevWorkflowTestFixture.SampleGraph, NodeCount: 3, DevWorkflowDefinitionSource.Seeded, "research-plan-approval");

        _ = await store.CreateDefinitionAsync(Seeded()).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.CreateDefinitionAsync(Seeded()),
                              "A re-seed must never duplicate a seeded template.")
                          .ConfigureAwait(false);

        // Manual rows leave the slug null, so any number of them coexist — that is what the filter on the index is for.
        _ = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Manual one", DevWorkflowTestFixture.SampleGraph, NodeCount: 1))
                       .ConfigureAwait(false);
        _ = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Manual two", DevWorkflowTestFixture.SampleGraph, NodeCount: 1))
                       .ConfigureAwait(false);
        AssertEx.Equal(expected: 3, (await store.ListDefinitionsAsync().ConfigureAwait(false)).Count);
    }

    /// <summary>
    ///     Two PUTs that each read version 1, which is what two people saving the same definition produce. The store's
    ///     read-then-check passes for both — neither read saw the other's write — so the row's concurrency token is the
    ///     only thing standing between the later write and a silent overwrite of the earlier one.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_RefusesTheWriterThatReadTheVersionAnotherWriteHasSinceBumped()
    {
        using var fixture = new DevWorkflowTestFixture();
        Guid definitionId;
        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var created = await DevWorkflowTestFixture.StoreFor(context)
                                                      .CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(),
                                                          "Original",
                                                          DevWorkflowTestFixture.SampleGraph,
                                                          NodeCount: 1))
                                                      .ConfigureAwait(false);
            definitionId = created.Id;
        }

        // Separate contexts: two writers on one would share a change tracker and never actually contend.
        await using var loserContext = fixture.CreateContext();
        await using var winnerContext = fixture.CreateContext();

        // The loser holds the row as it stood before the race — the state a request that has already loaded the
        // definition is in while the other request commits.
        _ = await loserContext.DevWorkflowDefinitions.SingleAsync(entity => entity.Id == definitionId).ConfigureAwait(false);

        _ = await DevWorkflowTestFixture.StoreFor(winnerContext)
                                        .UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(definitionId, ExpectedVersion: 1, "Winner"))
                                        .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => DevWorkflowTestFixture.StoreFor(loserContext)
                                                                                                    .UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(definitionId, ExpectedVersion: 1,
                                                                                                        "Loser")),
                              "The version check cannot see a write that landed after this writer read it, so the token has to.")
                          .ConfigureAwait(false);

        await using var readContext = fixture.CreateContext();
        var settled = await DevWorkflowTestFixture.StoreFor(readContext).GetDefinitionAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal("Winner", settled.Name, "The write that won has to survive: the silent overwrite IS the defect.");
        AssertEx.Equal(expected: 2, settled.Version, "And exactly one of the two edits may have bumped the version.");
    }

    /// <summary>
    ///     The runtime moves a work item's status while a human is editing its title, and it does so through the same
    ///     version token the edit is written under. The two writes touch disjoint fields, so the PATCH re-reads and
    ///     re-applies rather than failing: an operator renaming an item must not be told 409 — or handed a 500 — because
    ///     the dispatcher happened to start their run in the same instant.
    /// </summary>
    [Test]
    public async Task UpdateWorkItem_SurvivesTheRuntimeWritingStatusBetweenItsReadAndItsSave()
    {
        using var fixture = new DevWorkflowTestFixture();
        Guid workItemId;
        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var created = await DevWorkflowTestFixture.StoreFor(context)
                                                      .CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Original title", "Original request"))
                                                      .ConfigureAwait(false);
            workItemId = created.Id;
        }

        // Exactly the write a run start performs, on its own connection, landing inside the PATCH's save: the status
        // moves and the concurrency token moves with it.
        var interceptor = new CompetingWriteInterceptor(() => fixture.RawExecuteAsync("UPDATE dev_workflow_work_items SET status = 'Active', version = version + 1 WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", workItemId)));

        await using var editContext = fixture.CreateContext(interceptor);
        var updated = await DevWorkflowTestFixture.StoreFor(editContext)
                                                  .UpdateWorkItemAsync(new UpdateDevWorkflowWorkItemCommand(workItemId, DevWorkflowVersions.Any, "Renamed"))
                                                  .ConfigureAwait(false);

        AssertEx.Equal("Renamed", updated.Title, "The edit has to land: it raced a write whose fields it does not touch.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Active,
            updated.Status,
            "And the runtime's status write has to survive, rather than be overwritten by a re-apply against the row this edit first read.");
        AssertEx.Equal("Original request", updated.Request, "A PATCH that named only the title may not rewrite the request.");
    }

    /// <summary>
    ///     T5: a re-attempt empties every cost column, exactly as it empties the timestamps and the failure fields. The
    ///     columns describe one attempt, so carrying them forward would make the next attempt report the previous one's
    ///     spend — the earlier attempt's numbers live on its <c>node.retry.scheduled</c> event instead.
    /// </summary>
    [Test]
    public async Task Reattempt_ClearsTelemetry()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        var failed = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                    nodeRunId,
                                    version,
                                    DevWorkflowNodeRunStatus.Failed,
                                    FailureClass: "ProviderError",
                                    TerminalReason: "The provider refused the round.",
                                    Telemetry: new DevWorkflowNodeTelemetry(InputTokens: 1_200,
                                        OutputTokens: 340,
                                        ReasoningTokens: 90,
                                        EstimatedInputTokens: 1_500,
                                        ProviderCalls: 4,
                                        ToolCalls: 3,
                                        ToolSchemaTokens: 800,
                                        ToolNamesJson: """["read_document","search_web"]""",
                                        AgentTurnMs: 9_100,
                                        ServedModelName: "qwen3-27b",
                                        RouteJson: """{"satisfied":[],"dead":["review"],"gateAnswer":null,"truncated":false}""",
                                        WorkSessionSteps: 6)))
                                .ConfigureAwait(false);

        var settled = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1_200L, settled.InputTokens, "A terminal transition carrying telemetry writes it.");
        AssertEx.Equal("qwen3-27b", settled.ServedModelName, "The served model is what the provider answered with, written beside the counts.");

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           nodeRunId,
                           failed.Version,
                           DevWorkflowNodeRunStatus.Pending,
                           DetailJson: """{"attempt":1,"failureClass":"ProviderError"}""",
                           IncrementAttempt: true,
                           ClearWorkSession: true))
                       .ConfigureAwait(false);

        var reset = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        const string Because = "A re-attempt starts from a clean slate, and the cost columns are part of it.";
        AssertEx.Equal(expected: 2, reset.Attempt, "The re-attempt is the same row with a higher attempt number.");
        AssertEx.Null(reset.InputTokens, Because);
        AssertEx.Null(reset.OutputTokens, Because);
        AssertEx.Null(reset.ReasoningTokens, Because);
        AssertEx.Null(reset.EstimatedInputTokens, Because);
        AssertEx.Null(reset.ProviderCalls, Because);
        AssertEx.Null(reset.ToolCalls, Because);
        AssertEx.Null(reset.ToolSchemaTokens, Because);
        AssertEx.Null(reset.ToolNamesJson, Because);
        AssertEx.Null(reset.AgentTurnMs, Because);
        AssertEx.Null(reset.ServedModelName, Because);
        AssertEx.Null(reset.RouteJson, Because);
        AssertEx.Null(reset.WorkSessionSteps, Because);
    }

    /// <summary>
    ///     The two VRAM columns are a reading of the BOX at the run's load, not a running total: the first settle of an
    ///     attempt THAT CARRIES A READING owns them, so a re-settle that saw a later load cannot rewrite them — and a
    ///     re-attempt, whose ClearTelemetry empties both, re-opens them for its own first reading.
    /// </summary>
    [Test]
    public async Task ResettleWithinAnAttempt_KeepsTheFirstVramPair_AndAReattemptTakesTheNewOne()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        var first = await SettleAsync(version, freeBytes: 7_340_032_000L, admittedBytes: 5_368_709_120L).ConfigureAwait(false);
        var second = await SettleAsync(first, freeBytes: 1_073_741_824L, admittedBytes: 536_870_912L).ConfigureAwait(false);

        var resettled = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 7_340_032_000L, resettled.VramFreeAtLoadBytes, "The first settle's reading is the one that belongs to this attempt's load.");
        AssertEx.Equal(expected: 5_368_709_120L, resettled.VramAdmittedBytes, "And its partner, or the row would pair two different loads' figures.");

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     second,
                                     DevWorkflowNodeRunStatus.Pending,
                                     IncrementAttempt: true))
                                 .ConfigureAwait(false);
        _ = await SettleAsync(retried.Version, freeBytes: 1_073_741_824L, admittedBytes: 536_870_912L).ConfigureAwait(false);

        var reattempted = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1_073_741_824L, reattempted.VramFreeAtLoadBytes, "The re-attempt's clean slate is what re-opens the pair.");
        AssertEx.Equal(expected: 536_870_912L, reattempted.VramAdmittedBytes);

        async Task<long> SettleAsync(long expectedVersion, long freeBytes, long admittedBytes)
        {
            var result = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                        nodeRunId,
                                        expectedVersion,
                                        DevWorkflowNodeRunStatus.Failed,
                                        Telemetry: new DevWorkflowNodeTelemetry(VramFreeAtLoadBytes: freeBytes, VramAdmittedBytes: admittedBytes)))
                                    .ConfigureAwait(false);
            return result.Version;
        }
    }

    /// <summary>
    ///     "First settle wins" is really "first settle that CARRIES a reading wins": a settle whose collector answered
    ///     neither member — a remote model, a model this node never loaded — must leave the pair OPEN rather than
    ///     latch a null pair that no later settle of the attempt could then fill.
    /// </summary>
    [Test]
    public async Task SettleWithNoVramReading_LeavesThePairOpenForTheNextSettle()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        // A settle that measured no VRAM at all, but did collect something else.
        var afterBlank = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                        nodeRunId,
                                        version,
                                        DevWorkflowNodeRunStatus.Failed,
                                        Telemetry: new DevWorkflowNodeTelemetry(AgentTurnMs: 1_200)))
                                    .ConfigureAwait(false);

        var blank = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Null(blank.VramFreeAtLoadBytes, "A settle with no reading writes no reading.");
        AssertEx.Null(blank.VramAdmittedBytes, "Neither member, so neither column.");

        var settled = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     afterBlank.Version,
                                     DevWorkflowNodeRunStatus.Failed,
                                     Telemetry: new DevWorkflowNodeTelemetry(VramFreeAtLoadBytes: 7_340_032_000L, VramAdmittedBytes: 5_368_709_120L)))
                                 .ConfigureAwait(false);
        AssertEx.True(settled.Version > afterBlank.Version, "The second settle has to have landed for the assertion below to mean anything.");

        var filled = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 7_340_032_000L, filled.VramFreeAtLoadBytes, "The pair was still open, so the first reading of the attempt takes it.");
        AssertEx.Equal(expected: 5_368_709_120L, filled.VramAdmittedBytes, "And its partner, written with it.");
    }

    /// <summary>
    ///     FU3-4 race A, at the level that decides it. A human <c>Retry</c> and an automatic re-attempt spend the same
    ///     run-wide budget, and the automatic path used to check it on a read taken before its own write — so a Retry
    ///     recorded in that window spent the last slot and the re-attempt committed anyway. The budget now rides on the
    ///     transition command and is admitted inside its transaction, which is the only count that can refuse it.
    /// </summary>
    [Test]
    public async Task TransitionNodeRun_WithABudgetTheRunHasAlreadyPromised_IsRefusedInsideItsOwnTransaction()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var decidedId = Guid.NewGuid();
        var automaticId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, decidedId, "implement", seed.RunVersion).ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, automaticId, "validate", version).ConfigureAwait(false);

        // The human answer commits first and reserves the run's only re-attempt. Nothing has incremented an Attempt
        // yet, so a sum over Attempt still reads the budget as untouched — which is exactly the window the automatic
        // path's own pre-check would pass through.
        _ = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                           Guid.NewGuid(),
                           decidedId,
                           DevWorkflowVersions.Any,
                           Guid.NewGuid(),
                           DevWorkflowDecisionKind.Retry,
                           MaxTotalAttempts: 1))
                       .ConfigureAwait(false);

        TransitionDevWorkflowNodeRunCommand ReAttempt(int? budget) =>
            new(seed.RunId, automaticId, DevWorkflowVersions.Any, DevWorkflowNodeRunStatus.Pending, IncrementAttempt: true, MaxTotalAttempts: budget);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowRetryBudgetExceededException>(() => store.TransitionNodeRunAsync(ReAttempt(budget: 1)),
                                        "The recorded Retry has promised the run's only re-attempt, so the automatic one has nothing left to spend.")
                                    .ConfigureAwait(false);
        AssertEx.True(refusal.Message.Contains("as many re-attempts as this run allows", StringComparison.Ordinal),
            "The refusal reads the same whichever path ran into it.");
        AssertEx.Equal(expected: 1, (await store.GetNodeRunAsync(automaticId).ConfigureAwait(false)).Attempt, "A refused re-attempt writes nothing at all.");

        // A null budget is every other transition in the system, and it must go on behaving exactly as it did.
        _ = await store.TransitionNodeRunAsync(ReAttempt(budget: null)).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, (await store.GetNodeRunAsync(automaticId).ConfigureAwait(false)).Attempt, "No budget on the command means no budget check.");
    }

    /// <summary>
    ///     A routed fix loop costs the WHOLE cascade it resets, not one attempt, so the store charges it
    ///     <c>Resets.Count</c>. Admitting a fan-out one attempt at a time is how a run overspends its budget by the
    ///     width of its graph — the same accounting restart recovery does for the same reason.
    /// </summary>
    [Test]
    public async Task RouteRetry_WithABudgetTooSmallForTheWholeCascade_IsRefusedForItsCostRatherThanForOne()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var implementId = Guid.NewGuid();
        var validateId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, implementId, "implement", seed.RunVersion).ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, validateId, "validate", version).ConfigureAwait(false);

        RouteDevWorkflowRetryCommand Route(int? budget) =>
            new(new AppendDevWorkflowEventCommand(seed.RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, implementId, Guid.NewGuid()),
                [
                    new TransitionDevWorkflowNodeRunCommand(seed.RunId, validateId, DevWorkflowVersions.Any, DevWorkflowNodeRunStatus.Pending, IncrementAttempt: true),
                    new TransitionDevWorkflowNodeRunCommand(seed.RunId, implementId, DevWorkflowVersions.Any, DevWorkflowNodeRunStatus.Pending, IncrementAttempt: true)
                ],
                budget);

        _ = await AssertEx.ThrowsAsync<DevWorkflowRetryBudgetExceededException>(() => store.RouteRetryAsync(Route(budget: 1)),
                              "One free slot cannot pay for a two-row cascade, however affordable either row looks alone.")
                          .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, (await store.GetNodeRunAsync(validateId).ConfigureAwait(false)).Attempt, "A refused route writes none of its resets.");

        _ = await store.RouteRetryAsync(Route(budget: 2)).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, (await store.GetNodeRunAsync(validateId).ConfigureAwait(false)).Attempt);
        AssertEx.Equal(expected: 2, (await store.GetNodeRunAsync(implementId).ConfigureAwait(false)).Attempt, "A budget that covers the cascade admits all of it.");
    }

    /// <summary>Performs one competing write, on its own connection, inside the first save it intercepts.</summary>
    private sealed class CompetingWriteInterceptor(Func<Task> write) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await write().ConfigureAwait(false);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }
    }
}
