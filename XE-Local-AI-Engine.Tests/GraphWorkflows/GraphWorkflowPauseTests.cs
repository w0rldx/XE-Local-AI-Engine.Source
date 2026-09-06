namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pause: how a run parks on a person, and what one answer does to it.
///     <para>
///         Driven through the real command surface over the real store, because everything worth pinning here is about
///         the rows — which answer routes where, that one operation id writes once, and that the document a decision
///         stores is the same document the definition-time pre-flight evaluated. The 409 and 400 wire shapes are
///         asserted where they are observable, in <c>GraphWorkflowDecisionEndpointTests</c>.
///     </para>
/// </summary>
public sealed class GraphWorkflowPauseTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     Two writes, so a reader sees the moment the pause was REACHED as well as the moment it began waiting, and the
    ///     run follows its row with no new rule.
    /// </summary>
    [Test]
    public async Task ADispatchedPause_ParksTheRowAndTheRunAndAsksForADecision()
    {
        await using var harness = new GraphWorkflowHarness(Host);

        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, nodeRun.Status);
        AssertEx.Equal<GraphWorkflowDecisionKind?>(GraphWorkflowDecisionKind.Approve, nodeRun.PendingDecisionKind, "the pending ACT is named, singular.");
        AssertEx.True(nodeRun.StartedAtUtc is not null, "a pause that is waiting has started.");
        AssertEx.Null(nodeRun.OutputJson, "a pause's output is its answer, and it has none yet.");
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var trail = await harness.ReadEventTrailAsync(runId).ConfigureAwait(false);
        AssertEx.Contains(trail, GraphWorkflowEventTypes.GateRequested);
        AssertEx.Contains(trail, GraphWorkflowEventTypes.RunWaiting);
    }

    [Test]
    public async Task Approve_FiresTheApproveEdgeAndTheRunReachesItsEnd()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        var result = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowDecisionKind.Approve, result.Decision);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, result.NodeRunStatus, "both answers succeed the pause; routing is the edges' job.");
        AssertEx.Equal(GraphWorkflowRunStatus.Running, result.RunStatus, "the answer un-parks the run before the tick that acts on it.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "shipped").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "rejected").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task Reject_FiresTheRejectEdgeRatherThanFailingTheNode()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status,
            "a rejection reaches the run through an out-edge, not through a node failure.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "rejected").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "shipped").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A rejection whose branch arrives nowhere. The definition-time pre-flight is satisfied — the answer HAS an
    ///     edge — so the honest record of the stranding is the runtime's job.
    /// </summary>
    [Test]
    public async Task Reject_WhoseBranchReachesNoEnd_LeavesTheRunCancelledAsGateRejected()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseStrandedRejection, "review").ConfigureAwait(false);

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, run.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.GateRejected, run.FailureClass);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "stranded").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The same act arriving twice. One <c>gate.decided</c> event is the assertion that matters: a second write
    ///     would be a second audited human act for one click.
    /// </summary>
    [Test]
    public async Task TheSameOperationIdTwice_AnswersTheSameResultAndWritesTheDecisionOnce()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        var first = await harness.DecideAsync(runId, "review", operationId, GraphWorkflowDecisionKind.Approve, "looks fine").ConfigureAwait(false);
        var replay = await harness.DecideAsync(runId, "review", operationId, GraphWorkflowDecisionKind.Approve, "trimmed").ConfigureAwait(false);

        AssertEx.Equal(first.Decision, replay.Decision);
        AssertEx.Equal(first.NodeRunStatus, replay.NodeRunStatus);
        var decided = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(entry => entry.EventType == GraphWorkflowEventTypes.GateDecided);
        AssertEx.Equal(expected: 1, decided, "the comment is free text around the act, so a re-send with a different one is still one decision.");
    }

    /// <summary>A reused id naming another answer would read as success for a decision nobody took.</summary>
    [Test]
    public async Task TheSameOperationIdWithADifferentDecision_IsRefusedWithTheStandingAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        _ = await harness.DecideAsync(runId, "review", operationId, GraphWorkflowDecisionKind.Approve).ConfigureAwait(false);

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                                harness.DecideAsync(runId, "review", operationId, GraphWorkflowDecisionKind.Reject))
                            .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowDecisionKind.Approve, refusal.StandingDecision);
    }

    /// <summary>A DIFFERENT id on an answered pause is a second human act, refused with what stands rather than replayed.</summary>
    [Test]
    public async Task ADifferentOperationIdOnAnAnsweredPause_IsRefusedWithTheStandingAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Reject).ConfigureAwait(false);

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                                harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve))
                            .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowDecisionKind.Reject, refusal.StandingDecision);
    }

    /// <summary>
    ///     The reason the operation lookup is run-WIDE and comes first. Without it this id passes every check and then
    ///     violates the filtered unique index inside the write, as a database error rather than the conflict the API
    ///     promises.
    /// </summary>
    [Test]
    public async Task AnOperationIdReusedOnASecondPauseOfTheSameRun_IsRefusedRatherThanBreakingTheIndex()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoPausesInSequence, "first").ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        _ = await harness.DecideAsync(runId, "first", operationId, GraphWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await AdvanceUntilWaitingAsync(harness, runId, "second").ConfigureAwait(false);

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                                harness.DecideAsync(runId, "second", operationId, GraphWorkflowDecisionKind.Approve))
                            .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "first", StringComparison.Ordinal);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "second").ConfigureAwait(false)).Status,
            "the refused answer left the second pause exactly where it was.");
    }

    [Test]
    public async Task DecidingANodeRunThatIsNotWaiting_IsRefused()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoPausesInSequence).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "first", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "first", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve))
                          .ConfigureAwait(false);
    }

    /// <summary>A drain is already settling the row, and the answer would have no tick left to route it.</summary>
    [Test]
    public async Task DecidingWhileTheRunIsCancelling_IsRefused()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        await harness.CancelAsync(runId).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve))
                          .ConfigureAwait(false);
    }

    /// <summary>The graph is wrong, not the request: an answer nobody offered has no branch to travel.</summary>
    [Test]
    public async Task AnAnswerThePauseDoesNotOffer_IsRefused()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoPausesInSequence, "first").ConfigureAwait(false);

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "first", Guid.NewGuid(), GraphWorkflowDecisionKind.Reject))
                            .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "Approve", StringComparison.Ordinal);
    }

    [Test]
    public async Task ABlankCommentOnAPauseThatRequiresOne_IsABadRequest()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseRequiringComment, "review").ConfigureAwait(false);

        _ = await AssertEx
                  .ThrowsAsync<GraphWorkflowValidationException>(() =>
                      harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, "   "))
                  .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status,
            "a refused answer writes nothing.");
    }

    [Test]
    public async Task ACommentOverTheCap_IsABadRequest()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        _ = await AssertEx
                  .ThrowsAsync<GraphWorkflowValidationException>(() =>
                      harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, new string('c', count: 501)))
                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     The payload cap is strictly under the envelope budget, so an at-cap payload cannot pass here and then
    ///     overflow the document it is embedded in. Both halves are asserted: over it refuses, just under it composes.
    /// </summary>
    [Test]
    public async Task APayloadOverHalfTheOutputBudget_IsABadRequestAndOneJustUnderItComposes()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var half = harness.CurrentOptions().MaxOutputJsonBytes / 2;

        var oversized = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        _ = await AssertEx
                  .ThrowsAsync<GraphWorkflowValidationException>(() =>
                      harness.DecideAsync(oversized, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, PayloadOf(half + 1)))
                  .ConfigureAwait(false);

        var accepted = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);
        _ = await harness.DecideAsync(accepted, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, PayloadOf(half)).ConfigureAwait(false);

        var stored = await harness.ReadNodeRunAsync(accepted, "review").ConfigureAwait(false);
        using var document = JsonDocument.Parse(AssertEx.NotNull(stored.OutputJson));
        AssertEx.Equal(expected: half,
            document.RootElement.GetProperty("output").GetProperty("payload").GetRawText().Length,
            "a payload at the cap rides inside the envelope rather than replacing it.");
    }

    [Test]
    public async Task APayloadThatIsNotAJsonObject_IsABadRequest()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        _ = await AssertEx
                  .ThrowsAsync<GraphWorkflowValidationException>(() =>
                      harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, "[1,2,3]"))
                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     The producer guard. S0's pre-flight decides whether a graph may be saved by evaluating each out-edge against
    ///     <c>PauseOutputJson</c>; the run routes on the document a real answer stores. Both read
    ///     <c>output.decision</c>, and if the two spellings ever parted a graph that pre-flighted clean would route
    ///     nowhere.
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowDecisionKind.Approve)]
    [Arguments(GraphWorkflowDecisionKind.Reject)]
    public async Task TheStoredAnswerAndThePreFlightDocument_SpellTheDecisionTheSameWay(GraphWorkflowDecisionKind decision)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness, GraphWorkflowGraphs.PauseTwoDecisions, "review").ConfigureAwait(false);

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), decision, "why not").ConfigureAwait(false);

        var stored = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        using var written = JsonDocument.Parse(AssertEx.NotNull(stored.OutputJson));
        using var preflight = JsonDocument.Parse(GraphWorkflowStateMachine.PauseOutputJson(decision));
        AssertEx.Equal(AssertEx.NotNull(preflight.RootElement.GetProperty("output").GetProperty("decision").GetString()),
            written.RootElement.GetProperty("output").GetProperty("decision").GetString(),
            "one spelling, or the pre-flight and the run disagree in exactly the case that matters.");
        AssertEx.Equal("why not", written.RootElement.GetProperty("output").GetProperty("comment").GetString());
    }

    /// <summary>A JSON object of exactly <paramref name="bytes" /> UTF-8 bytes, padded in its one value.</summary>
    private static string PayloadOf(int bytes)
    {
        const string prefix = "{\"note\":\"";
        const string suffix = "\"}";
        return prefix + new string('p', bytes - prefix.Length - suffix.Length) + suffix;
    }

    private static async Task<Guid> ParkedRunAsync(GraphWorkflowHarness harness, string graphJson, string nodeKey)
    {
        var runId = await harness.StartRunAsync(graphJson).ConfigureAwait(false);
        await AdvanceUntilWaitingAsync(harness, runId, nodeKey).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     Ticks the run until <paramref name="nodeKey" /> is parked. Bounded by the harness's own quiescence loop
    ///     rather than by a wait, so a pause that never opens fails here instead of hanging.
    /// </summary>
    private static async Task AdvanceUntilWaitingAsync(GraphWorkflowHarness harness, Guid runId, string nodeKey)
    {
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false)).Status,
            $"the run was expected to park on '{nodeKey}'.");
    }
}
