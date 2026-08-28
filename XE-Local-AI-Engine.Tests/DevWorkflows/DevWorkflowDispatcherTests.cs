namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The dispatcher over real rows: what one tick decides, and what a run's whole life looks like from the outside.
///     <para>
///         Every advance here is explicit. Nothing sleeps, nothing polls, and the assertions are about the event trail
///         as much as the end state — a runtime whose audit log is the replay authority has to be tested on what it
///         wrote, not only on where it landed.
///     </para>
/// </summary>
public sealed class DevWorkflowDispatcherTests
{
    /// <summary>A human gate on its own: one node, one question, one answer, and the run is done.</summary>
    private const string GateOnly = """
                                    {
                                      "schemaVersion": 1,
                                      "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                      "edges": []
                                    }
                                    """;

    /// <summary>
    ///     The Phase A2 gate. A human-gate-only definition runs to completion: the run materializes its entry node,
    ///     stops on the human, and finishes on the answer — with the work item tracking it the whole way.
    /// </summary>
    [Test]
    public async Task AHumanGateDefinition_RunsToCompletionOnItsDecision()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Pending, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var waiting = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, waiting.Status);
        AssertEx.Equal(DevWorkflowDecisionKind.Approve, waiting.PendingDecisionKind, "the gate says which answer it expects.");
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a run waiting on a human blocks its work item, and the runtime writes that — no client ever does.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, settled.Status);
        AssertEx.Null(settled.PendingDecisionKind, "the pending marker is cleared when the decision lands.");
        AssertEx.Contains(AssertEx.NotNull(settled.OutputJson), "\"decision\":\"Approve\"");

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Completed, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);

        // 'run.resumed' is the run entering WaitingForApproval: the store reads the three in-flight intents and the
        // approval wait as a resumption of the run's own narrative rather than as life events of their own.
        AssertEx.Equal("run.created, node.materialized, run.started, node.queued, node.started, gate.requested, run.resumed, gate.decided, node.completed, run.completed",
            await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "the whole run has to be replayable from the log, in order.");
    }

    /// <summary>
    ///     Sequence numbers are the replay contract: a client reading everything after watermark N must be able to trust
    ///     that it will see each row once and in order.
    ///     <para>
    ///         Strictly increasing rather than contiguous, and that distinction is the contract: one counter per run
    ///         serves the events AND the node runs and artifacts, so an event feed legitimately steps over the numbers
    ///         the rows it describes took.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunsEventSequenceIsStrictlyIncreasing()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var sequences = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Select(static entry => entry.Sequence).ToList();

        AssertEx.Equal(expected: 10, sequences.Count);
        AssertEx.Equal(string.Join(", ", sequences.Order()), string.Join(", ", sequences), "the feed arrives in sequence order.");
        AssertEx.Equal(sequences.Count, sequences.Distinct().Count(), "and no two rows ever share a watermark.");
        AssertEx.Equal(sequences[^1], (await harness.ReadRunAsync(runId).ConfigureAwait(false)).LastSequence, "the run's counter is the high-water mark.");
    }

    /// <summary>
    ///     Ticking a settled run is not merely harmless, it is the normal case: the sweep visits every live run and the
    ///     signal channel deliberately drops nothing it cannot afford to re-deliver.
    /// </summary>
    [Test]
    public async Task AdvancingAQuiescentRunWritesNothing()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var trail = await harness.ReadEventTrailAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "a run waiting on a human has nothing for the dispatcher to do.");
        AssertEx.Equal(trail, await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "and it wrote no event either.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "and a completed run is left alone entirely.");
    }

    /// <summary>
    ///     The gate's answer is its output, and routing is the edges' job. A rejection therefore does not fail the gate:
    ///     it takes the reject branch, and the definition decides what that means.
    /// </summary>
    [Test]
    public async Task AGateDecisionRoutesThroughItsOutEdges()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ApprovalBranches).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.RequestChanges).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped,
            (await harness.ReadNodeRunAsync(runId, "ship").ConfigureAwait(false)).Status,
            "the branch whose condition did not match is dead, and its node run says so.");

        // 'revise' is an Agent node, which this build has no executor for — the run is blocked on it, honestly, rather
        // than left in a queue nothing drains.
        var revise = await harness.ReadNodeRunAsync(runId, "revise").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, revise.Status);
        AssertEx.Equal("Configuration", revise.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(revise.TerminalReason), "no executor on this node can run yet");
    }

    /// <summary>
    ///     A gate answered in a way none of its branches accepts ends the run, and ends it through the drain.
    ///     <para>
    ///         Completing (every downstream skipped) and failing (nothing failed) would each be a lie about a refused
    ///         approval, and writing the terminal directly would strand whatever else the run still had live. Both are
    ///         asserted, because the second is invisible from the end state alone.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AGateAnswerNoBranchAcceptsCancelsTheRunThroughTheDrain()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ApprovalBranches).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Neither out-edge takes 'Reject': one wants Approve, the other RequestChanges.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);

        AssertEx.True(await harness.AdvanceAsync(runId).ConfigureAwait(false) > 0);
        var draining = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelling, draining.Status, "the terminal is reached through the drain, never written over live rows.");
        AssertEx.Equal("GateRejected", draining.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(draining.TerminalReason), "'approve'", message: "the reason names the gate, or nobody can tell which one refused.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "the gate did its job; it is the run that has nowhere left to go.");
    }

    /// <summary>
    ///     A skip has to reach all the way down, or a run stalls on a node whose upstream will never arrive. Three
    ///     levels, because two would not distinguish propagation from a single hop.
    /// </summary>
    [Test]
    public async Task ASkippedBranchPropagatesToEveryNodeBelowIt()
    {
        await using var harness = new DevWorkflowHarness();

        // The gate node succeeds with no upstream, so its 'passed' condition finds nothing and fails closed — which is
        // exactly the "route on evidence that is not there" case the whole chain below must not take.
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ThreeLevelChain).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("first: Skipped, gate: Succeeded, second: Skipped, third: Skipped",
            string.Join(", ", nodeRuns.OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal).Select(static nodeRun => $"{nodeRun.NodeKey}: {nodeRun.Status}")));

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a run whose every branch condition was false completes; that is a real outcome, and the log says which.");
    }

    /// <summary>
    ///     Cancelling is fire-and-forget: the command writes an intent and returns, and the dispatcher settles it. The
    ///     terminal is reached through that drain and never written directly — a terminal written over live node runs
    ///     would strand them under a run no tick ever visits again.
    /// </summary>
    [Test]
    public async Task CancellingDrainsTheLiveNodeRunsBeforeTheRunReachesCancelled()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, gate.Status, "the human wait was live, so the drain settled it.");
        AssertEx.Equal("Cancelled", gate.FailureClass);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A pause is meant to be resumed, so it leaves the human wait alone rather than tearing it down. The run says
    ///     <c>Pausing</c> until it has settled, because the command that asked for it has already returned.
    /// </summary>
    [Test]
    public async Task PausingLeavesAHumanWaitStandingAndResumingPicksItBackUp()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "a durable human wait survives a pause untouched.");

        // The decision is taken while the run is paused, which the dispatcher must defer rather than lose.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "a paused run advances nothing, decision or no decision.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "the decision was deferred, not lost: the first tick after the resume applied it.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The intervention answers, on a node run this build cannot execute. <c>Skip</c> routes around it; the run then
    ///     completes because a skipped node run is terminal and does not block completion.
    /// </summary>
    [Test]
    public async Task AHumanSkipOnABlockedNodeRunLetsTheRunFinish()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a node needing intervention is a human wait, and the run says so.");

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("approve: Skipped, plan: Skipped, research: Skipped",
            string.Join(", ", nodeRuns.OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal).Select(static nodeRun => $"{nodeRun.NodeKey}: {nodeRun.Status}")));
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     <c>Abandon</c> is the other end of the same decision: the node run fails for good, and the run fails with it —
    ///     which maps the work item to Blocked, because a failed run needs attention rather than being done.
    /// </summary>
    [Test]
    public async Task AHumanAbandonFailsTheNodeRunAndTheRunWithIt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Abandon).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Failed, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The operator's request has to reach the first node, and there is no run-level input column: every entry node
    ///     run is seeded with it at materialization, which is where the objective composer will read it from.
    /// </summary>
    [Test]
    public async Task TheEntryNodeRunIsSeededWithTheWorkItemsRequest()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval, "Explain the KV cache").ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).InputJson), "Explain the KV cache");

        // Every node run of the graph exists from the start — that is what keeps terminalization honest, since a run
        // with rows still to come would otherwise read as "nothing live" and complete early. Only the ENTRY node is
        // seeded with the request, though: the rest are fed by their upstreams.
        AssertEx.Equal(expected: 3, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count);
        AssertEx.Null((await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).InputJson);
    }

    /// <summary>
    ///     A run whose pinned graph cannot be routed fails at its first tick.
    ///     <para>
    ///         The graph is validated again at run start rather than trusted from the save, because an agent definition
    ///         can be deleted in between. Left Pending the run would be swept forever; moved to Running it would claim
    ///         work nothing can do.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunWhosePinnedGraphCannotBeRoutedFailsAtItsFirstTick()
    {
        await using var harness = new DevWorkflowHarness();

        // Two entry nodes: routable JSON, unroutable graph. Nothing validates a definition on the way in yet, so this
        // is exactly the shape a stale or hand-written definition would present at run start.
        var runId = await harness.StartRunAsync("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Gate"},{"nodeKey":"b","nodeType":"Gate"}],"edges":[]}""")
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Failed, run.Status);
        AssertEx.Equal("Configuration", run.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "exactly one entry node");
        AssertEx.Empty(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), "nothing was materialized, so nothing has to be cleaned up.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a failed run needs attention rather than being done.");
    }

    /// <summary>
    ///     The parsed graph is cached per run and invalidated by the run's revision, so a run that ticks many times
    ///     decrypts and parses its graph exactly once.
    /// </summary>
    [Test]
    public async Task TheParsedGraphIsCachedAcrossTicksAndDroppedWhenTheRunTerminalizes()
    {
        await using var harness = new DevWorkflowHarness();
        var cache = harness.Graphs;
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        var before = cache.ParseCount;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "several ticks, one parse.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "and the graph never changed, so it never re-parsed.");

        // Terminalizing drops the entry, so a later tick on the same id parses again rather than answering from a cache
        // nothing would ever invalidate.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "a terminal run is not parsed at all.");
    }

    /// <summary>
    ///     A restart loses the dispatcher and its cache and nothing else, because the run is entirely in the database.
    ///     Simulated the way the reconciler will be: same rows, fresh dispatcher.
    /// </summary>
    [Test]
    public async Task ARunSurvivesTheDispatcherBeingReplacedMidFlight()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);

        await using (var replacement = harness.CreateReplacementDispatcher())
        {
            _ = await replacement.AdvanceOnceAsync(runId, CancellationToken.None).ConfigureAwait(false);
            _ = await replacement.AdvanceOnceAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a dispatcher that never saw the run's earlier ticks finishes it from the rows alone.");
    }
}
