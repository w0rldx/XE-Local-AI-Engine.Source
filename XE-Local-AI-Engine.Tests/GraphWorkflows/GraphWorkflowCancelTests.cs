namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
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
[Category(TestCategories.Integration)]
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
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear);

        await harness.CancelAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, (await harness.ReadRunAsync(runId)).Status);

        _ = await harness.AdvanceAsync(runId);

        var run = await harness.ReadRunAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, run.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled,
            run.FailureClass,
            "the drain classifies the terminal it writes: a cancelled run reading None records nothing at all about why it stopped.");
        foreach (var nodeRun in await harness.ReadNodeRunsAsync(runId))
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
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear);

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
        var events = await harness.ReadEventsAsync(runId);
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
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear);

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceAsync(runId);

        var events = await harness.ReadEventsAsync(runId);
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
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear);
        var runId = await harness.StartRunThroughTheStoreAsync(definitionId, "{ not json at all", [("start", GraphWorkflowNodeKind.Start)]);

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "start")).Status);
    }

    /// <summary>
    ///     A cancel mid-run settles the siblings a lane does not own directly — there is nothing to ask them — and
    ///     leaves what already finished alone.
    /// </summary>
    [Test]
    public async Task CancellingMidRun_SettlesThePendingSiblingsAndLeavesTheFinishedOnesAlone()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll);
        _ = await harness.AdvanceAsync(runId);
        _ = await harness.AdvanceAsync(runId);

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceAsync(runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "start")).Status,
            "a cancel does not rewrite what already succeeded.");

        var sibling = await harness.ReadNodeRunAsync(runId, "merge");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, sibling.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, sibling.FailureClass);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
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
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear);
        await harness.CancelAsync(runId);

        await harness.AdvanceSafelyAsync(runId);
        AssertEx.True(harness.WasSignalled(runId), "the drain wrote transitions, so it asked for the tick that would finish it.");

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId));
        await harness.AdvanceSafelyAsync(runId);
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

        var flight = await lane.TryStartAsync(nodeRunId, attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None);

        AssertEx.NotNull(flight);
        AssertEx.True(lane.IsInFlight(nodeRunId));
        AssertEx.True(await lane.StopAsync(nodeRunId), "the first ask is the one that actually cancels.");
        AssertEx.False(await lane.StopAsync(nodeRunId), "the entry is still there, and asking again is not work.");
        AssertEx.False(await lane.StopAsync(Guid.NewGuid()), "and neither is asking about a row nothing is driving.");
    }

    /// <summary>
    ///     A full lane is queueing, not failure: nothing is started, nothing is written, and the next tick asks again.
    /// </summary>
    [Test]
    public async Task TheInFlightLane_RefusesToStartWhenEverySlotIsHeld()
    {
        await using var lane = new GraphWorkflowInFlightLane<int>(slots: 1);

        _ = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None);

        var refused = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), (_, _) => Task.FromResult(result: 2), CancellationToken.None);

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
            _ = await lane.TryStartAsync(nodeRunId, attempt: 1, Guid.NewGuid(), Parked, CancellationToken.None);
        }

        await lane.ForgetSupersededAsync([Row(superseded, GraphWorkflowNodeRunStatus.Running, attempt: 2), Row(current, GraphWorkflowNodeRunStatus.Running, attempt: 1)]);

        AssertEx.False(lane.IsInFlight(superseded), "the row is on its second attempt and this entry belongs to the first.");
        AssertEx.True(lane.IsInFlight(current));

        await lane.DiscardAsync(current);
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
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Wedges });
        var runId = await RunToARunningAgentAsync(harness, instructions);
        var invocationId = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "analyze")).InvocationId?.ToString(),
            "a Running agent row carries the invocation its turn was minted with.");

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceAsync(runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "analyze")).Status,
            "ask, do not settle: the turn is still winding down.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(expected: 1, harness.Invocations.Cancelled.Count(cancelled => cancelled.ToString() == invocationId), "the runner is asked once, not once per tick.");

        harness.Invocations.Release(Guid.Parse(invocationId));

        var analyze = await AdvanceUntilCancelledAsync(harness, runId);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, analyze.FailureClass);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
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
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Wedges });
        var runId = await RunToARunningAgentAsync(harness, instructions);

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceAsync(runId);
        _ = harness.WasSignalled(runId);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId), "the stop was already asked, so the repeat has nothing to write.");
        await harness.AdvanceSafelyAsync(runId);
        AssertEx.False(harness.WasSignalled(runId), "and writing nothing is what stops it asking for another tick.");
        AssertEx.Equal(expected: 1, harness.Invocations.Cancelled.Count, "three ticks of drain, one ask.");

        foreach (var nodeRun in await harness.ReadNodeRunsAsync(runId))
        {
            if (nodeRun.InvocationId is { } invocationId)
            {
                harness.Invocations.Release(invocationId);
            }
        }

        _ = await AdvanceUntilCancelledAsync(harness, runId);
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
                                                  """);
        _ = await harness.AdvanceAsync(runId);
        _ = await harness.AdvanceAsync(runId);
        _ = await harness.AdvanceAsync(runId);
        await harness.Invocations.WhenRunningAsync(instructions).WaitAsync(TestBudgets.Contended);
        _ = await harness.AdvanceAsync(runId);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "analyze")).Status);
        return runId;
    }

    private static async Task<GraphWorkflowNodeRunSnapshot> AdvanceUntilCancelledAsync(GraphWorkflowHarness harness, Guid runId, int maxTicks = 40)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze");
            if (GraphWorkflowStateMachine.IsTerminal(nodeRun.Status))
            {
                return nodeRun;
            }

            _ = await harness.AdvanceAsync(runId);
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
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 1;
    }

    private static GraphWorkflowNodeRunSnapshot Row(Guid nodeRunId, GraphWorkflowNodeRunStatus status, int attempt) =>
        new()
        {
            Id = nodeRunId,
            RunId = Guid.NewGuid(),
            NodeKey = "work",
            Kind = GraphWorkflowNodeKind.Agent,
            Status = status,
            Attempt = attempt,
            PendingDecisionKind = null,
            DecisionOperationId = null,
            DecidedBySubject = null,
            FailureClass = GraphWorkflowFailureClass.None,
            Error = null,
            InputJson = null,
            OutputJson = null,
            InvocationId = null,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            UpdatedAtUtc = 0
        };

    /// <summary>
    ///     S2's Codex finding, pinned: a pause ANSWERED between the drain tick's snapshot and its cancel write. The
    ///     gate holds the drain in exactly that window while a real decide is attempted through the real command
    ///     surface.
    ///     <para>
    ///         What is pinned here is the OUTCOME, not one guard: no decision is lost, because none can land. The run
    ///         is already <c>Cancelling</c> when the drain runs, and TWO independent checks read that —
    ///         <c>GraphWorkflowRunService.DecideAsync</c> before it asks the store, and
    ///         <c>GraphWorkflowStore.DecideNodeRunAsync</c> inside its own transaction. Either alone produces this
    ///         result, so the test does not name a single load-bearing one; it fails on the
    ///         <c>DecisionOperationId</c> the drain would be writing over if BOTH went away.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADecisionRacingTheDrainWrite_IsRefusedRatherThanOverwritten()
    {
        var gate = new GraphWorkflowDrainGate();
        await using var harness = GraphWorkflowHarness.PrivateHost(services =>
        {
            // Wrapped AROUND the container's own registration rather than rebuilt from its parts: the publishing
            // decorator stays underneath, so the drain still announces every write it commits.
            var registered = services.Single(descriptor => descriptor.ServiceType == typeof(IGraphWorkflowStore));
            var build = registered.ImplementationFactory
                        ?? throw new AssertionException("The graph workflow store is expected to be registered through a factory.");
            _ = services.Remove(registered);
            services.AddScoped<IGraphWorkflowStore>(provider => new GatedDrainGraphWorkflowStore((IGraphWorkflowStore)build(provider), gate));
        });

        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions);
        await harness.AdvanceUntilAsync(runId,
                         async () => (await harness.ReadNodeRunAsync(runId, "review")).Status
                                     == GraphWorkflowNodeRunStatus.WaitingForApproval,
                         "the pause never reached WaitingForApproval.");

        await harness.CancelAsync(runId);

        // The tick runs detached so the test can act inside it. Nothing here waits on a clock: the gate is the
        // rendezvous, and the drain is standing in front of its cancel write when Reached completes.
        var tick = harness.AdvanceAsync(runId);
        GraphWorkflowRunConflictException refusal;
        try
        {
            // Bounded: the project has no global test timeout, so a drain that stopped routing the pause through this
            // write would otherwise hang the whole CI leg instead of failing it.
            await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));
            refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve));
        }
        finally
        {
            gate.Release();
        }

        _ = await tick;
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Contains(refusal.Message, "Cancelling", message: "the operator is told the run stopped, not that somebody else answered.");
        var review = await harness.ReadNodeRunAsync(runId, "review");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, review.Status);
        AssertEx.Null(review.DecisionOperationId, "no decision landed, so the drain overwrote none.");
        AssertEx.Null(review.OutputJson, "a pause the drain cancelled wrote no decision document.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
    }
}

/// <summary>
///     A gate the test opens, shared across DI scopes because the dispatcher ticks in one of its own. It holds the
///     drain at the exact instant S2's Codex finding named — after the tick has read the node runs and decided this one
///     is live, before it writes <c>Cancelled</c> over it.
/// </summary>
internal sealed class GraphWorkflowDrainGate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _trips;

    /// <summary>Completes when the drain is standing in front of its first cancel write.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets that write go through.</summary>
    public void Release() =>
        _ = _released.TrySetResult();

    /// <summary>Trips ONCE — the first cancel write only, so the rest of the drain runs at full speed.</summary>
    public Task WaitAsync()
    {
        if (Interlocked.Increment(ref _trips) != 1)
        {
            return Task.CompletedTask;
        }

        _ = _reached.TrySetResult();
        return _released.Task;
    }
}

/// <summary>
///     The real store with ONE seam: the drain's first <c>Cancelled</c> write waits on
///     <see cref="GraphWorkflowDrainGate" />. Everything else forwards untouched, so every check either side of that
///     write is the production one.
/// </summary>
internal sealed class GatedDrainGraphWorkflowStore : IGraphWorkflowStore
{
    private readonly IGraphWorkflowStore _inner;
    private readonly GraphWorkflowDrainGate _gate;

    public GatedDrainGraphWorkflowStore(IGraphWorkflowStore inner, GraphWorkflowDrainGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public async Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TargetStatus == GraphWorkflowNodeRunStatus.Cancelled)
        {
            await _gate.WaitAsync();
        }

        return await _inner.TransitionNodeRunAsync(command, cancellationToken);
    }

    public Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateDefinitionAsync(command, cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateDefinitionAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListDefinitionsAsync(cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.GetDefinitionAsync(definitionId, cancellationToken);

    public Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.DeleteDefinitionAsync(definitionId, cancellationToken);

    public Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.StartRunAsync(command, cancellationToken);

    public Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        _inner.FindRunByRequestAsync(requestId, cancellationToken);

    public Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunsAsync(status, limit, cancellationToken);

    public Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default) =>
        _inner.CountActiveRunsAsync(probeLimit, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsByConversationAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default) =>
        _inner.ListRunsByConversationAsync(conversationId, limit, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot?> FindConversationDecisionAsync(Guid conversationId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindConversationDecisionAsync(conversationId, operationId, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListUnpublishedNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListUnpublishedNodeRunsAsync(runId, cancellationToken);

    public Task<GraphWorkflowMutationResult?> MarkNodeRunPublishedAsync(Guid runId, Guid nodeRunId, Guid messageId, CancellationToken cancellationToken = default) =>
        _inner.MarkNodeRunPublishedAsync(runId, nodeRunId, messageId, cancellationToken);

    public Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.TransitionRunAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListNodeRunsAsync(runId, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        _inner.GetNodeRunAsync(runId, nodeKey, cancellationToken);

    public Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.DecideNodeRunAsync(command, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken);

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        _inner.AppendEventAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        _inner.ListEventsAsync(runId, afterSeq, limit, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListInterruptedNodeRunsAsync(cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default) =>
        _inner.ReconcileNonTerminalNodeRunsAsync(sanitizedReason, verdicts, unjudged, cancellationToken);
}
