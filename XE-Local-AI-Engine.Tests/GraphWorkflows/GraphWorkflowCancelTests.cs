namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancel drain, and the in-flight registry contract that keeps it from spinning.
///     <para>
///         This build ships no lane, so every live row a drain meets is one it settles directly. The half of the drain
///         that ASKS is asserted against the lane class itself, which is where the hot-loop fix lives.
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

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        foreach (var nodeRun in await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
        {
            AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, nodeRun.Status);
            AssertEx.Null(nodeRun.StartedAtUtc, $"'{nodeRun.NodeKey}' was never dispatched, so it never started.");
        }
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

        var refused = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), _ => Task.FromResult(result: 2), CancellationToken.None).ConfigureAwait(false);

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

        foreach (var nodeRunId in new[] { superseded, current })
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
    ///     Work that never lands on its own, so the only thing that ends it is the lane's own token — which is exactly
    ///     what the stop and discard paths are about.
    /// </summary>
    private static async Task<int> Parked(CancellationToken cancellationToken)
    {
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
