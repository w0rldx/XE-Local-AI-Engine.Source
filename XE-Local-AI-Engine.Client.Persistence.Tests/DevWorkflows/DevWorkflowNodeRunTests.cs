namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevWorkflowNodeRunTests
{
    /// <summary>
    ///     T-14: a retry bumps the attempt in place — still one row per (run, node key) — and the history the schema no
    ///     longer holds is reconstructible from the event log, which is what makes that trade honest.
    /// </summary>
    [Test]
    public async Task RetryingANode_BumpsTheAttemptInPlaceAndLeavesTheHistoryInTheEventLog()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "implement", seed.RunVersion).ConfigureAwait(false);

        var firstSessionId = Guid.NewGuid();
        var attached = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, version, firstSessionId)).ConfigureAwait(false);
        var running = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, nodeRunId, attached.Version, DevWorkflowNodeRunStatus.Running))
                                 .ConfigureAwait(false);
        var failed = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     running.Version,
                                     DevWorkflowNodeRunStatus.Failed,
                                     FailureClass: "ToolCommandFailed",
                                     TerminalReason: "build failed"))
                                .ConfigureAwait(false);

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                      nodeRunId,
                                      failed.Version,
                                      DevWorkflowNodeRunStatus.Pending,
                                      IncrementAttempt: true))
                                 .ConfigureAwait(false);
        var secondSessionId = Guid.NewGuid();
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, retried.Version, secondSessionId, CountsAsResume: true))
                       .ConfigureAwait(false);

        var nodeRuns = await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false);
        var nodeRun = nodeRuns.Single();
        AssertEx.Equal(expected: 1, nodeRuns.Count, "A retry must never create a second row for the same node key.");
        AssertEx.Equal(expected: 2, nodeRun.Attempt);
        AssertEx.Equal(expected: 1, nodeRun.SessionResumes);
        AssertEx.Equal(secondSessionId, nodeRun.WorkSessionId);
        AssertEx.Null(nodeRun.StartedAtUtc, "A re-attempt starts clean, or the UI shows it running since its first try.");
        AssertEx.Null(nodeRun.FailureClass, "A node run trying again must not still report the previous attempt's failure class.");
        AssertEx.Null(nodeRun.TerminalReason, "Nor its previous reason — that belongs to the node.failed event, not to a row that is about to run again.");

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryScheduled), "The retry itself must be in the log.");
        AssertEx.Equal(expected: 2,
            events.Count(item => item.EventType == DevWorkflowEventTypes.WorkSessionAttached),
            "Both attempts' sessions must be reconstructible from the log, since the row only keeps the current one.");
        AssertEx.True(events.Any(item => item.EventType == DevWorkflowEventTypes.NodeFailed && item.Outcome == "failed"));
    }

    /// <summary>T-15: one decision per attempt, several over a node-run's life, and a replayed operation reads its own body back.</summary>
    [Test]
    public async Task Decisions_AreOnePerAttemptAndReplayReturnsTheRecordedBody()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "approval", seed.RunVersion, DevWorkflowNodeType.HumanGate)
                                                  .ConfigureAwait(false);

        var retryOperationId = Guid.NewGuid();
        var retryDecision = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                                            Guid.NewGuid(),
                                            nodeRunId,
                                            version,
                                            retryOperationId,
                                            DevWorkflowDecisionKind.Retry,
                                            "Try once more.",
                                            DecidedBySubject: "operator-subject"))
                                       .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(
                () => store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                    Guid.NewGuid(),
                    nodeRunId,
                    retryDecision.Version,
                    Guid.NewGuid(),
                    DevWorkflowDecisionKind.Approve)),
                "A second decision on the SAME attempt must be rejected.")
            .ConfigureAwait(false);

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                      nodeRunId,
                                      retryDecision.Version,
                                      DevWorkflowNodeRunStatus.Pending,
                                      IncrementAttempt: true))
                                 .ConfigureAwait(false);
        _ = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                            Guid.NewGuid(),
                            nodeRunId,
                            retried.Version,
                            Guid.NewGuid(),
                            DevWorkflowDecisionKind.Approve))
                       .ConfigureAwait(false);

        var decisions = await store.ListDecisionsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, decisions.Count, "One node run legitimately accumulates a decision per attempt.");
        AssertEx.Equal(expected: 1, decisions[0].Attempt);
        AssertEx.Equal(expected: 2, decisions[1].Attempt);

        // The replay read: a repeated POST has to answer with the same BODY, and the mutation result carries no
        // decision id, subject or decided-at to answer with.
        var replayed = AssertEx.NotNull(await store.FindDecisionByOperationAsync(seed.RunId, retryOperationId).ConfigureAwait(false));
        AssertEx.Equal(DevWorkflowDecisionKind.Retry, replayed.Decision);
        AssertEx.Equal("Try once more.", replayed.Comment);
        AssertEx.Equal("operator-subject", replayed.DecidedBySubject, "Without the subject the audit can say a gate was decided but not by whom.");
        AssertEx.Null(await store.FindDecisionByOperationAsync(seed.RunId, Guid.NewGuid()).ConfigureAwait(false));
    }

    /// <summary>Queued and Running are distinct states with distinct timestamps, which is what makes the UI's progress honest.</summary>
    [Test]
    public async Task QueuedAndRunning_AreDistinctStatesWithTheirOwnTimestampsAndReason()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        var queued = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     version,
                                     DevWorkflowNodeRunStatus.Queued,
                                     QueueReason: "awaiting-agent-slot"))
                                .ConfigureAwait(false);
        var afterQueue = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal("awaiting-agent-slot", afterQueue.QueueReason);
        AssertEx.True(afterQueue.QueuedAtUtc is not null);
        AssertEx.Null(afterQueue.StartedAtUtc, "Queued is not running, and the row must not pretend otherwise.");

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, nodeRunId, queued.Version, DevWorkflowNodeRunStatus.Running))
                       .ConfigureAwait(false);
        var afterStart = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Null(afterStart.QueueReason, "A running node run is not waiting in any queue.");
        AssertEx.True(afterStart.StartedAtUtc is not null);
    }

    /// <summary>A work session may have exactly one owning node run — the reverse lookup depends on it.</summary>
    [Test]
    public async Task AttachWorkSession_RefusesASecondOwner()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var firstNodeRunId = Guid.NewGuid();
        var secondNodeRunId = Guid.NewGuid();
        var version = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                      seed.RunVersion,
                                      Guid.NewGuid(),
                                      [
                                          new DevWorkflowNodeRunSeed(firstNodeRunId, "research", DevWorkflowNodeType.Agent),
                                          new DevWorkflowNodeRunSeed(secondNodeRunId, "plan", DevWorkflowNodeType.Agent)
                                      ]))
                                 .ConfigureAwait(false);

        var sessionId = Guid.NewGuid();
        var attached = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, firstNodeRunId, version.Version, sessionId))
                                  .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(
                () => store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, secondNodeRunId, attached.Version, sessionId)),
                "One session, one owner.")
            .ConfigureAwait(false);
    }

    /// <summary>Materializing the same node key twice is a transition error, not a raw constraint violation.</summary>
    [Test]
    public async Task Materialize_RejectsANodeKeyTheRunAlreadyCarries()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, Guid.NewGuid(), "research", seed.RunVersion).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(
                () => store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                    version,
                    Guid.NewGuid(),
                    [new DevWorkflowNodeRunSeed(Guid.NewGuid(), "research", DevWorkflowNodeType.Agent)])),
                "The node key is the node run's identity within a run.")
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     A rewritten graph and the node runs it explains land in one transaction, the revision bumps once, and the
    ///     definition row is byte-unchanged — which is what keeps re-running a definition unaffected by expansion.
    /// </summary>
    [Test]
    public async Task MaterializeWithARewrittenGraph_BumpsTheRevisionOnceAndLeavesTheDefinitionAlone()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        const string Expanded = """{"schemaVersion":1,"nodes":[{"nodeKey":"implement#1","nodeType":"DevTask"}],"edges":[]}""";
        var result = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                     seed.RunVersion,
                                     Guid.NewGuid(),
                                     [new DevWorkflowNodeRunSeed(Guid.NewGuid(), "implement#1", DevWorkflowNodeType.DevTask, MaterializationIndex: 0)],
                                     Expanded))
                                .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.GraphRevision, "One materialization, one revision.");

        var run = await store.GetRunAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(Expanded, run.GraphJson, "The run's own pinned graph is the single source of routing truth, so it is what changes.");

        var definition = await store.GetDefinitionAsync(seed.DefinitionId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowTestFixture.SampleGraph, definition.GraphJson, "The definition must be byte-unchanged after an expansion.");
        AssertEx.Equal(expected: 1, definition.Version);

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.GraphChanged), "Exactly one graph.changed event records the rewrite.");
        AssertEx.Empty(events.Where(item => item.EventType == DevWorkflowEventTypes.NodeMaterialized),
            "A rewrite reads as graph.changed; node.materialized is the initial, graph-unchanged case.");
    }
}
