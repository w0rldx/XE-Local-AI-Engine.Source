namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cross-node fix loop (X9): a downstream node that fails does not re-attempt itself, it re-runs the upstream
///     node whose work it was judging, and everything downstream of THAT node re-runs with it.
///     <para>
///         Every test takes a host of its own. The scripted sandbox is a container singleton keyed by node key and these
///         fixtures share node keys, so a shared host would let one test's script answer another's node run.
///     </para>
/// </summary>
public sealed class DevWorkflowFixLoopTests
{
    /// <summary>A project id on the work item, because a graph with tool nodes in it is only startable with one.</summary>
    private static readonly Guid DevelopmentProjectId = Guid.NewGuid();

    /// <summary>
    ///     The §10.2 X9 row on the fan-out it is specified against: <c>Implement → {Lint, Test} → Join</c> with
    ///     <c>Test</c> routing its failure back to <c>Implement</c>. <c>Lint</c> SUCCEEDED and is reset anyway — that is
    ///     the F2 ruling, and the reason for it is that a passing lint of an implementation that no longer exists is a
    ///     stale answer presented as a current one.
    /// </summary>
    [Test]
    public async Task AFailedCheckReRunsTheNodeItWasJudgingAndEveryNodeDownstreamOfIt()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoop, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        // Round one: the implementation lands, both checks run, and the test fails.
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var firstSession = await harness.ReadSessionIdAsync(runId, "implement").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var routed = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retry.routed");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "\"from\":\"test\"");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "\"to\":\"implement\"");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "ToolCommandFailed");

        var implement = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, implement.Attempt, "the node that failed is not the node that is re-attempted.");
        AssertEx.NotEqual(firstSession,
            await harness.ReadSessionIdAsync(runId, "implement").ConfigureAwait(false),
            "a re-run target drives a NEW session, so it does not resume what it did last time.");

        var lint = await harness.ReadNodeRunAsync(runId, "lint").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, lint.Attempt, "a SUCCEEDED sibling is reset too: its answer was about an implementation that is being replaced.");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "test").ConfigureAwait(false)).Attempt);
        AssertEx.Equal(expected: 1,
            (await harness.ReadNodeRunAsync(runId, "join").ConfigureAwait(false)).Attempt,
            "a node run that never started needs no reset, and an attempt recorded on it would be one the run never made.");

        // Round two: the same graph runs forward again through the edges it already had.
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 2, harness.Tools.Ran.Count(static nodeKey => nodeKey == "lint"), "the lint really did run again.");
        AssertEx.Equal(expected: 2, harness.Tools.Ran.Count(static nodeKey => nodeKey == "test"));
        AssertEx.Equal(expected: 2, harness.Agent.Created.Count, "one session per round of the loop.");
    }

    /// <summary>
    ///     What the re-run is TOLD. The failure that sent the run back to it travels in the target's inputs, so the
    ///     objective the next attempt is composed from names what went wrong rather than asking for the same work again.
    /// </summary>
    [Test]
    public async Task TheReRunTargetIsToldWhatFailedAndWhereItFailed()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoop, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implement = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        var input = AssertEx.NotNull(implement.InputJson);
        AssertEx.Contains(input, "\"priorFailureNode\":\"test\"");
        AssertEx.Contains(input, "\"testsFailed\":3", message: "the failing node's own output document is what travels, counts and all.");

        var objective = harness.Agent.Objectives[^1];
        AssertEx.Contains(objective, "priorFailure", message: "and the objective the re-run is composed from renders it.");
        AssertEx.Contains(objective, "tests_failed");
    }

    /// <summary>
    ///     The pinned supersede case, which nothing could reach until the fix loop re-ran a producer: the re-run's
    ///     artifact supersedes its own earlier version, and what a downstream node produced from the version it replaced
    ///     is flagged. Staleness is observed through the event, never through the artifact cursor — an artifact's
    ///     sequence is allocated at insert and a staleness flip never re-stamps it.
    /// </summary>
    [Test]
    public async Task AReRunProducerSupersedesItsOwnArtifactAndFlagsWhatConsumedTheOldVersion()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("verify", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FixLoopOverAConsumedArtifact, developmentProjectId: DevelopmentProjectId)
                                 .ConfigureAwait(false);

        // Round one: validate reports, the agent consumes that report and writes one of its own, and verify fails.
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "summarize").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, consumed.Count, "the agent node recorded what it was given, which is what makes the staleness rule reachable at all.");
        _ = await harness.SaveAgentArtifactAsync(runId, "summarize", "summary.md", "The validation passed.").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "summarize").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        // Round two: validate re-runs and its new report supersedes the one the summary was written from.
        await harness.SettleAgentAsync(runId, "summarize").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var reports = artifacts.Where(static artifact => artifact.ProducingNodeKey == "validate").OrderBy(static artifact => artifact.Version).ToList();
        AssertEx.Equal(expected: 2, reports.Count, "the re-run versioned the same lineage rather than starting a new one.");
        AssertEx.False(reports[0].IsLatest, "the first version is no longer current.");
        AssertEx.True(reports[1].IsLatest);

        var promoted = artifacts.Single(static artifact => artifact.ProducingNodeKey == "summarize");
        AssertEx.True(promoted.IsStale, "what the agent wrote was written from a report that has since been replaced.");
        AssertEx.Equal(reports[1].Id, promoted.StaleBecauseArtifactId, "and the row says which version replaced it.");
        AssertEx.Equal("superseded-input", promoted.StaleReason);

        // Announced on the FEED, because the artifact cursor cannot carry it: an artifact's sequence is allocated at
        // insert and a staleness flip never re-stamps it. Every re-run producer supersedes its own report, so the run
        // carries one of these per re-run node; the one that matters here is the one naming the report the summary was
        // written from.
        var marked = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "artifact.stale.marked").ToList();
        AssertEx.Equal(expected: 1,
            marked.Count(entry => entry.DetailJson?.Contains(reports[0].Id.ToString(), StringComparison.OrdinalIgnoreCase) == true),
            "the flip that flagged the summary is on the feed exactly once.");
    }

    /// <summary>
    ///     Two checks that both route to the same producer and both fail in the same round. Exactly one of them routes:
    ///     the first one settled resets the other along with everything else downstream, and the pass that was running
    ///     for it is dropped rather than settled onto an attempt it never ran. Neither waits for the other, which is
    ///     what a fix loop that deferred to its siblings would do — for ever, since each is the other's sibling.
    /// </summary>
    [Test]
    public async Task TwoChecksThatBothRouteInOneRoundResolveToASingleReRun()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("checkone", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("checktwo", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TwoChecksBothRoutingBack, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.retry.routed"),
            "one round of the loop is one re-run, however many of its checks failed.");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Attempt);

        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "and the round finished rather than the two checks waiting on each other.");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "checkone").ConfigureAwait(false)).Attempt);
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "checktwo").ConfigureAwait(false)).Attempt);
    }

    /// <summary>
    ///     A gate that is still open when the check beside it routes its failure upstream. The approval it is asking for
    ///     is about work that is being replaced, so it is re-asked from a fresh attempt rather than left standing over
    ///     the round that has been thrown away — and answering the old round would approve something that no longer
    ///     exists.
    /// </summary>
    [Test]
    public async Task AGateStillWaitingForAnAnswerIsReAskedRatherThanLeftOverAReplacedRound()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FixLoopBesideAnOpenGate, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, gate.Status, "the open gate went back to the start of a new attempt rather than wedging the route.");
        AssertEx.Equal(expected: 2, gate.Attempt);
        AssertEx.Null(gate.PendingDecisionKind, "and it is no longer asking for the answer it wanted about the round that was replaced.");

        // The second round re-opens it, and the answer it takes then is one about work that still exists.
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var reopened = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, reopened.Status);
        AssertEx.Equal(DevWorkflowDecisionKind.Approve, reopened.PendingDecisionKind);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The reset reaching work that is still running. An agent sibling's re-attempt clears its <c>WorkSessionId</c>,
    ///     so a session left driving would hold the node's one invocation slot with nothing pointing at it — and the
    ///     fresh attempt would then queue behind the very session it supersedes. Both live siblings are stood down
    ///     before their rows move, and the superseded session is CANCELLED rather than deleted: it ran, so it is this
    ///     run's audit evidence.
    /// </summary>
    [Test]
    public async Task AResetSubtreeStandsDownTheLiveWorkItWouldOtherwiseOrphan()
    {
        await using var harness = new DevWorkflowHarness();
        var held = harness.Tools.Hold("slow");
        harness.Tools.Answer("check", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.LiveSiblingsBesideAFixLoop, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        // Round one: the implementation lands and all three of its successors are admitted by the same tick.
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        var reviewSession = await harness.ReadSessionIdAsync(runId, "review").ConfigureAwait(false);
        var slow = await harness.ReadNodeRunAsync(runId, "slow").ConfigureAwait(false);
        var check = await harness.ReadNodeRunAsync(runId, "check").ConfigureAwait(false);
        AssertEx.True(harness.ToolLane.IsInFlight(slow.Id), "the held sibling really is holding a sandbox slot when the check routes.");

        // The check's verdict lands and sends the run back to the implementation, resetting both live siblings.
        await harness.ToolLane.WaitForCompletionAsync(check.Id).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var review = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, review.Attempt, "the agent sibling was reset along with everything else downstream of the target.");
        AssertEx.Null(review.WorkSessionId, "and the reset released the session it was driving.");
        AssertEx.Contains(harness.Agent.Calls, ("cancel", reviewSession), "which is only honest if the session was ASKED to stop first.");
        AssertEx.Equal(AgentWorkSessionStatus.Cancelled,
            (await harness.Agent.GetAsync(reviewSession).ConfigureAwait(false)).Status,
            "the superseded session still EXISTS — it ran, so it is audit evidence — and it is no longer holding the node's slot.");
        AssertEx.False(harness.ToolLane.IsInFlight(slow.Id), "the sandbox sibling's pass was dropped rather than left running for an attempt nobody will poll.");

        // Round two drives fresh work rather than resuming what it superseded.
        held.Release();
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.NotEqual(reviewSession,
            await harness.ReadSessionIdAsync(runId, "review").ConfigureAwait(false),
            "the re-attempt gets a NEW session, so it does not resume the context that was thrown away.");
    }

    /// <summary>
    ///     A routed retry costs the target's attempt AND one for every node run it resets, so the run-wide budget has to
    ///     be able to afford the whole cascade. Admitting it one attempt at a time is how a fan-out spends more than the
    ///     run allows by the width of its graph — and the node that failed is the one that then asks for a human, while
    ///     the target is left exactly as it was.
    /// </summary>
    [Test]
    public async Task ARoutedRetryTheBudgetCannotAffordBlocksTheFailingNodeAndLeavesTheTargetAlone()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "2"));
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoop, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var failing = await harness.ReadNodeRunAsync(runId, "test").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, failing.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, failing.FailureClass, "the cascade costs three re-attempts and the run allows two.");
        AssertEx.Equal(expected: 1, failing.Attempt, "a refused route spends nothing.");

        var target = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, target.Status, "the target is left exactly as it was; the human decides what happens next.");
        AssertEx.Equal(expected: 1, target.Attempt);
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retry.routed"));
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     <c>GRAPH-C4-4</c>: a fix loop the definition bounded stops when its own cap is spent, not when the run-wide
    ///     budget runs out. The first failure routes; the second is refused, and the target is left where it was.
    /// </summary>
    [Test]
    public async Task AFixLoopBoundedByItsDefinitionBlocksWhenTheCapIsSpent()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing(), FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoopBounded, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Attempt, "the first failure routes: one re-run is what the cap allows.");

        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var failing = await harness.ReadNodeRunAsync(runId, "test").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, failing.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, failing.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(failing.TerminalReason), "fix loop has been re-run 1 time, which is as many as it allows");
        AssertEx.Equal(expected: 2,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Attempt,
            "and the target is left exactly where the one allowed re-run left it, with attempts of its own to spare.");
    }

    /// <summary>
    ///     An operator's <c>Retry</c> raises the same attempt counter the cap is derived from, and it is bounded by the
    ///     run-wide budget rather than by this. So it is subtracted: a person's decision must not spend a loop the
    ///     definition gave the machine.
    /// </summary>
    [Test]
    public async Task AnOperatorRetryDoesNotCountTowardTheFixLoopCap()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing(), FakeDevWorkflowToolCommands.Passing(), FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test",
            FakeDevWorkflowToolCommands.Failing(),
            FakeDevWorkflowToolCommands.Failing(),
            FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoopBounded, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "test", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var failing = await harness.ReadNodeRunAsync(runId, "test").ConfigureAwait(false);
        AssertEx.Equal(expected: 3, failing.Attempt, "the person's retry really did spend an attempt on the row.");
        AssertEx.Contains(AssertEx.NotNull(failing.TerminalReason),
            "fix loop has been re-run 1 time",
            message: "and the count is still ONE: the loop ran once, and the operator's attempt is not one of its iterations.");
    }

    /// <summary>
    ///     The byte-identical default: a definition naming no cap keeps today's accounting, where only the target's own
    ///     attempts and the run-wide budget bound the loop (ruling D9).
    /// </summary>
    [Test]
    public async Task AFixLoopWithNoCapKeepsTodaysAccounting()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing(), FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(Uncapped(), developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 3,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Attempt,
            "the second failure routes as well, exactly as it did before this rule existed.");
        AssertEx.Equal(expected: 2,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.retry.routed"));
    }

    /// <summary>The same graph with its per-loop cap taken off, so the two accountings are compared on one shape.</summary>
    private static string Uncapped() =>
        DevWorkflowGraphs.FanOutFixLoopBounded.Replace(", \"maxLoopIterations\": 1", string.Empty, StringComparison.Ordinal);
}
