namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancel drain, and the in-flight registry contract that keeps it from spinning.
///     <para>
///         An inline row a drain meets is one it settles directly; an Agent row it can only ASK, and the two tests at
///         the end are about that half — including the repeat that must write nothing, which is the hot-loop fix seen
///         from the dispatcher rather than from the lane it lives in.
///     </para>
/// </summary>
public sealed class GraphWorkflowCancelTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     A run cancelled before any tick started it is drained rather than started: the run reads <c>Cancelling</c>,
    ///     not <c>Pending</c>, so the start branch never fires and no node is ever dispatched.
    /// </summary>
    [Test]
    public async Task CancellingARunThatNeverTicked_SettlesItWithoutDispatchingAnything()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, run.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled,
            run.FailureClass,
            "the drain classifies the terminal it writes: a cancelled run reading None records nothing at all about why it stopped.");
        foreach (var nodeRun in await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
        {
            AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, nodeRun.Status);
            AssertEx.Null(nodeRun.StartedAtUtc, $"'{nodeRun.NodeKey}' was never dispatched, so it never started.");
        }
    }

    /// <summary>
    ///     One cancel, one <c>run.cancelled</c>. The command writes the event at the moment the cancel was ASKED for,
    ///     and the drain's settle records none — a second row would make the log read as two cancels of one run.
    /// </summary>
    [Test]
    public async Task AFullCancel_WritesExactlyOneRunCancelledEvent()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1,
            events.Count(static entry => string.Equals(entry.EventType, GraphWorkflowEventTypes.RunCancelled, StringComparison.Ordinal)),
            "the request writes the event; the settle that follows it is the run row's business.");
    }

    /// <summary>
    ///     A run cancelled from <c>Pending</c>, with nothing live to drain, still gets exactly one <c>run.cancelled</c>:
    ///     the settle happens on the same tick as the drain and must not add a second.
    /// </summary>
    [Test]
    public async Task ACancelFromPending_WritesExactlyOneRunCancelledEvent()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(static entry => string.Equals(entry.EventType, GraphWorkflowEventTypes.RunCancelled, StringComparison.Ordinal)));
    }

    /// <summary>
    ///     A cancelling run whose pinned graph no longer parses still settles. The drain needs no graph, and the state
    ///     machine has no <c>Cancelling → Failed</c> edge — so a run that reached the unroutable branch instead of the
    ///     drain would sit <c>Cancelling</c> with nothing able to move it.
    /// </summary>
    [Test]
    public async Task ACancellingRunWhosePinnedGraphNoLongerParses_StillSettlesCancelled()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        var runId = await harness.StartRunThroughTheStoreAsync(definitionId, "{ not json at all", [("start", GraphWorkflowNodeKind.Start)]).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "start").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A cancel mid-run settles the siblings a lane does not own directly — there is nothing to ask them — and
    ///     leaves what already finished alone.
    /// </summary>
    [Test]
    public async Task CancellingMidRun_SettlesThePendingSiblingsAndLeavesTheFinishedOnesAlone()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "start").ConfigureAwait(false)).Status,
            "a cancel does not rewrite what already succeeded.");

        var sibling = await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, sibling.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, sibling.FailureClass);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The drain does not spin. The tick that settles it is productive and asks for another; the tick after it
    ///     writes nothing and asks for nothing, because a run that has finished cancelling is finished.
    /// </summary>
    [Test]
    public async Task TheDrain_DoesNotSpin()
    {
        // A private host: the signal channel is the dispatcher's own, and a sibling draining it would decide the answer.
        await using var harness = new GraphWorkflowHarness();
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        await harness.CancelAsync(runId).ConfigureAwait(false);

        await harness.AdvanceSafelyAsync(runId).ConfigureAwait(false);
        AssertEx.True(harness.WasSignalled(runId), "the drain wrote transitions, so it asked for the tick that would finish it.");

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false));
        await harness.AdvanceSafelyAsync(runId).ConfigureAwait(false);
        AssertEx.False(harness.WasSignalled(runId), "a drained run writes nothing on a repeat, so nothing re-signals.");
    }

    /// <summary>
    ///     The hot-loop fix itself. The entry lives until a poll SEES the work land, so a cancelling drain reaches the
    ///     stop every tick until then; answering yes each time would count as a written transition, re-signal, and spin
    ///     the run for the whole duration of the work.
    /// </summary>
    [Test]
    public async Task TheInFlightLane_AsksOnceAndAnswersNoOnARepeat()
    {
        await using var lane = new GraphWorkflowInFlightLane<int>(slots: 1);
        var nodeRunId = Guid.NewGuid();

        var flight = await lane.TryStartAsync(nodeRunId, attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None).ConfigureAwait(false);

        AssertEx.NotNull(flight);
        AssertEx.True(lane.IsInFlight(nodeRunId));
        AssertEx.True(await lane.StopAsync(nodeRunId).ConfigureAwait(false), "the first ask is the one that actually cancels.");
        AssertEx.False(await lane.StopAsync(nodeRunId).ConfigureAwait(false), "the entry is still there, and asking again is not work.");
        AssertEx.False(await lane.StopAsync(Guid.NewGuid()).ConfigureAwait(false), "and neither is asking about a row nothing is driving.");
    }

    /// <summary>
    ///     A full lane is queueing, not failure: nothing is started, nothing is written, and the next tick asks again.
    /// </summary>
    [Test]
    public async Task TheInFlightLane_RefusesToStartWhenEverySlotIsHeld()
    {
        await using var lane = new GraphWorkflowInFlightLane<int>(slots: 1);

        _ = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None).ConfigureAwait(false);

        var refused = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), (_, _) => Task.FromResult(result: 2), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(refused, "the slot count is the bound, and a full lane simply answers no.");
    }

    /// <summary>
    ///     An entry whose row has moved on is dropped before anything is polled — a retry reaches a row WITHOUT coming
    ///     through the lane driving it, and an answer about the attempt before is not an answer about this one.
    /// </summary>
    [Test]
    public async Task TheInFlightLane_ForgetsAnEntryWhoseRowMovedOn()
    {
        await using var lane = new GraphWorkflowInFlightLane<int>(slots: 2);
        var superseded = Guid.NewGuid();
        var current = Guid.NewGuid();

        foreach (var nodeRunId in new[]
                 {
                     superseded,
                     current
                 })
        {
            _ = await lane.TryStartAsync(nodeRunId, attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None).ConfigureAwait(false);
        }

        await lane.ForgetSupersededAsync([Row(superseded, GraphWorkflowNodeRunStatus.Running, attempt: 2), Row(current, GraphWorkflowNodeRunStatus.Running, attempt: 1)])
                  .ConfigureAwait(false);

        AssertEx.False(lane.IsInFlight(superseded), "the row is on its second attempt and this entry belongs to the first.");
        AssertEx.True(lane.IsInFlight(current));

        await lane.DiscardAsync(current).ConfigureAwait(false);
        AssertEx.False(lane.IsInFlight(current), "removing the entry is the load-bearing half of a discard, not the cancel.");
    }


    /// <summary>
    ///     A cancel that meets a running agent turn ASKS, and does not settle: only the lane knows what stopping its
    ///     turn costs, so the row's terminal is written on the tick after the turn actually lands.
    /// </summary>
    [Test]
    public async Task CancellingMidAgentTurn_AsksTheLaneAndSettlesOnTheTickAfterTheTurnLands()
    {
        const string instructions = "cancel-mid-agent-turn";

        // A private agent host: a wedged turn holds the node-wide invocation slot, and the signal channel this asserts
        // on is the dispatcher's own.
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Wedges));
        var runId = await RunToARunningAgentAsync(harness, instructions).ConfigureAwait(false);
        var invocationId = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false)).InvocationId?.ToString(),
            "a Running agent row carries the invocation its turn was minted with.");

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false)).Status,
            "ask, do not settle: the turn is still winding down.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1, harness.Invocations.Cancelled.Count(cancelled => cancelled.ToString() == invocationId), "the runner is asked once, not once per tick.");

        harness.Invocations.Release(Guid.Parse(invocationId));

        var analyze = await AdvanceUntilCancelledAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, analyze.FailureClass);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The hot-loop fix, seen from the dispatcher. The entry lives until a poll SEES the turn land, so a cancelling
    ///     drain reaches the stop on every tick until then — and a lane answering yes each time would be counted as a
    ///     written transition, re-signalled, and would spin the run for the whole duration of the model turn.
    /// </summary>
    [Test]
    public async Task TheDrain_DoesNotSpinWhileAnAgentTurnIsStillWindingDown()
    {
        const string instructions = "drain-does-not-spin";
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Wedges));
        var runId = await RunToARunningAgentAsync(harness, instructions).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = harness.WasSignalled(runId);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "the stop was already asked, so the repeat has nothing to write.");
        await harness.AdvanceSafelyAsync(runId).ConfigureAwait(false);
        AssertEx.False(harness.WasSignalled(runId), "and writing nothing is what stops it asking for another tick.");
        AssertEx.Equal(expected: 1, harness.Invocations.Cancelled.Count, "three ticks of drain, one ask.");

        foreach (var nodeRun in await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
        {
            if (nodeRun.InvocationId is { } invocationId)
            {
                harness.Invocations.Release(invocationId);
            }
        }

        _ = await AdvanceUntilCancelledAsync(harness, runId).ConfigureAwait(false);
    }

    /// <summary>A run of a linear Start → Agent → End graph, ticked until its agent turn is Running.</summary>
    private static async Task<Guid> RunToARunningAgentAsync(GraphWorkflowHarness harness, string instructions)
    {
        var runId = await harness.StartRunAsync($$"""
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "key": "start", "kind": "Start" },
                                                      { "key": "analyze", "kind": "Agent", "config": { "instructions": "{{instructions}}" } },
                                                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                    ],
                                                    "edges": [
                                                      { "key": "e1", "from": "start", "to": "analyze" },
                                                      { "key": "e2", "from": "analyze", "to": "done" }
                                                    ]
                                                  }
                                                  """)
                                 .ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.Invocations.WhenRunningAsync(instructions).WaitAsync(TestBudgets.Contended).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false)).Status);
        return runId;
    }

    private static async Task<GraphWorkflowNodeRunSnapshot> AdvanceUntilCancelledAsync(GraphWorkflowHarness harness, Guid runId, int maxTicks = 40)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
            if (GraphWorkflowStateMachine.IsTerminal(nodeRun.Status))
            {
                return nodeRun;
            }

            _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        }

        throw new AssertionException($"Run {runId} left its agent node unsettled after {maxTicks} ticks.");
    }

    /// <summary>
    ///     Work that never lands on its own, so the only thing that ends it is the lane's own token — which is exactly
    ///     what the stop and discard paths are about. It flips the lease box the lane hands it, the way a real turn
    ///     does once it holds the node-wide slot.
    /// </summary>
    private static async Task<int> Parked(StrongBox<bool> leaseAcquired, CancellationToken cancellationToken)
    {
        leaseAcquired.Value = true;
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private static GraphWorkflowNodeRunSnapshot Row(Guid nodeRunId, GraphWorkflowNodeRunStatus status, int attempt) =>
        new(nodeRunId,
            Guid.NewGuid(),
            "work",
            GraphWorkflowNodeKind.Agent,
            status,
            attempt,
            PendingDecisionKind: null,
            DecisionOperationId: null,
            DecidedBySubject: null,
            GraphWorkflowFailureClass.None,
            Error: null,
            InputJson: null,
            OutputJson: null,
            InvocationId: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            UpdatedAtUtc: 0);
}
