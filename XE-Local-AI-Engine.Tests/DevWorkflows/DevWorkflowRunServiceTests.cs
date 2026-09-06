namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The command surface: what a caller can ask of a run, what it is refused, and what a repeat of the same request
///     does.
/// </summary>
public sealed class DevWorkflowRunServiceTests
{
    private const string GateOnly = """
                                    {
                                      "schemaVersion": 1,
                                      "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                      "edges": []
                                    }
                                    """;

    /// <summary>One agent node, for the delete path: a gate owns no work session, and releasing those is half the job.</summary>
    private const string SingleAgent = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }],
                                         "edges": []
                                       }
                                       """;

    [ClassDataSource<DevWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required DevWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     A start pins the graph, gives every node a row, seeds the entry rows with what was asked, and tells the
    ///     dispatcher — the last of which is what keeps a fresh run from sitting visibly Pending until the next sweep.
    /// </summary>
    [Test]
    public async Task StartingARun_MaterializesTheGraphSeedsTheRequestAndSignals()
    {
        // A private host: WasSignalled DRAINS the dispatcher's signal channel, so a concurrent sibling's drain
        // could take this run's signal before this test reads it.
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly, "Explain the inference path.").ConfigureAwait(false);

        var detail = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, """{"depth":"deep"}""", Guid.NewGuid()))
                                  .ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Pending, detail.Run.Status, "a run is Pending until the runtime has accepted it; anything else would claim work nothing is doing.");
        AssertEx.Equal(expected: 1, detail.NodeRuns.Count);
        AssertEx.True(harness.WasSignalled(detail.Run.Id), "without the signal the run waits out a whole sweep interval for no reason a reader can see.");

        var entry = detail.NodeRuns.Single();
        AssertEx.Contains(AssertEx.NotNull(entry.InputJson), "\"workItemRequest\":\"Explain the inference path.\"");
        AssertEx.Contains(AssertEx.NotNull(entry.InputJson), "depth", message: "the caller's seed reaches the entry node run rather than being dropped.");
        AssertEx.Contains(AssertEx.NotNull(entry.InputJson), "deep");

        _ = await harness.AdvanceUntilQuiescentAsync(detail.Run.Id).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(detail.Run.Id).ConfigureAwait(false)).Status);
    }

    /// <summary>A repeated start is the same start: it answers with the run it already created, not with a second one.</summary>
    [Test]
    public async Task StartingARunTwiceWithOneOperationId_ReturnsTheSameRun()
    {
        // A private host: the run count is the whole database's, so a sibling's run would be counted here.
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        var first = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, operationId)).ConfigureAwait(false);
        var replay = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, operationId)).ConfigureAwait(false);

        AssertEx.Equal(first.Run.Id, replay.Run.Id);
        AssertEx.Equal(expected: 1, (await harness.ListRunIdsAsync().ConfigureAwait(false)).Count);
        AssertEx.Equal(expected: 1, replay.NodeRuns.Count, "the replay must not materialize a second set of rows either.");
    }

    /// <summary>One live run per work item: a second start is a conflict, because two runs would each claim the item.</summary>
    [Test]
    public async Task StartingASecondLiveRunOnOneWorkItem_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowRunInFlightException>(() =>
                                  harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())),
                              "Its own conflict type, because the operator's next move differs from any other invalid transition: wait for the live run, or cancel it.")
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A graph that runs commands in a repository needs one. Checked at run start rather than at save, because the
    ///     same definition is legitimately reusable by a work item that does name a project.
    ///     <para>
    ///         Both repository-bound node types, because they need the project for different things — the tool node runs
    ///         its commands in that project's workspace, and the implementation node drives that project's task — and a
    ///         rule that held for only one of them would be a hole rather than a rule.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("Tool")]
    [Arguments("DevTask")]
    public async Task StartingARepositoryBoundGraphOnAProjectlessWorkItem_IsRefused(string nodeType)
    {
        // A private host, for the same reason: "no run at all" is an assertion about the whole database.
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync($$"""
                                                                             {
                                                                               "schemaVersion": 1,
                                                                               "nodes": [{ "nodeKey": "validate", "nodeType": "{{nodeType}}" }],
                                                                               "edges": []
                                                                             }
                                                                             """)
                                                      .ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowValidationException>(() =>
                                        harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "validate", message: "the refusal names the nodes that need the project, not merely that one is missing.");
        AssertEx.Empty(await harness.ListRunIdsAsync().ConfigureAwait(false), "a refused start leaves no run behind.");
    }

    /// <summary>An archived template is hidden from new runs; the runs that already used it are unaffected.</summary>
    [Test]
    public async Task StartingFromAnArchivedDefinition_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        await harness.ArchiveDefinitionAsync(definitionId).ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowValidationException>(() =>
                                        harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "archived");
    }

    /// <summary>
    ///     The three lifecycle commands are fire-and-forget: each writes the intent and returns the CURRENT state, which
    ///     is why the <c>-ing</c> statuses exist at all.
    /// </summary>
    [Test]
    public async Task TheLifecycleCommands_WriteTheirIntentAndSignal()
    {
        // A private host: WasSignalled drains the shared signal channel (see StartingARun_... above).
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var pausing = await harness.WithRunServiceAsync(service => service.PauseAsync(runId, Guid.NewGuid())).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Pausing, pausing.Run.Status, "the command has not landed yet, and saying Paused would claim one that has not.");
        AssertEx.True(harness.WasSignalled(runId));
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var resumed = await harness.WithRunServiceAsync(service => service.ResumeAsync(runId, Guid.NewGuid())).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Running, resumed.Run.Status);

        var cancelling = await harness.WithRunServiceAsync(service => service.CancelAsync(runId, Guid.NewGuid())).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelling, cancelling.Run.Status);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>A command the run's status forbids is a conflict, not a silent no-op.</summary>
    [Test]
    public async Task ResumingARunThatIsNotPaused_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() => harness.WithRunServiceAsync(service => service.ResumeAsync(runId, Guid.NewGuid())))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A decision records who took it and comes back with what it recorded, so a repeated POST can answer with the
    ///     same body rather than with a conflict about the node run having moved on because of it.
    /// </summary>
    [Test]
    public async Task DecidingAGate_RecordsTheSubjectAndReplaysTheSameDecision()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;
        var operationId = Guid.NewGuid();

        var decided = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                       nodeRunId,
                                       operationId,
                                       DevWorkflowDecisionKind.Approve,
                                       "Looks right.",
                                       payloadJson: null,
                                       "operator@localhost.test"))
                                   .ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowDecisionKind.Approve, decided.Decision.Decision);
        AssertEx.Equal("operator@localhost.test", decided.Decision.DecidedBySubject, "the audit has to say who approved, not only that someone did.");
        AssertEx.Equal("Looks right.", decided.Decision.Comment);

        // The run moves on, and the replay still answers with the decision rather than complaining about where the row
        // has got to since.
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var replay = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                      nodeRunId,
                                      operationId,
                                      DevWorkflowDecisionKind.Approve,
                                      "Looks right.",
                                      payloadJson: null,
                                      "operator@localhost.test"))
                                  .ConfigureAwait(false);

        AssertEx.Equal(decided.Decision.Id, replay.Decision.Id);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, replay.Detail.Run.Status);
        AssertEx.Equal(expected: 1, (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "gate.decided"));
    }

    /// <summary>
    ///     A NEW operation id at an answered gate is not the idempotent replay a repeated one is: it is a second human
    ///     act on a closed gate. The refusal carries what already stands, so the operator is told what happened rather
    ///     than only that their click failed.
    /// </summary>
    [Test]
    public async Task DecidingAnAlreadyAnsweredGateUnderANewOperationId_IsRefusedWithTheStandingDecision()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;

        _ = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                             nodeRunId,
                             Guid.NewGuid(),
                             DevWorkflowDecisionKind.RequestChanges,
                             comment: null,
                             payloadJson: null,
                             "operator@localhost.test"))
                         .ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowGateAlreadyDecidedException>(() =>
                                        harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                            nodeRunId,
                                            Guid.NewGuid(),
                                            DevWorkflowDecisionKind.Approve,
                                            comment: null,
                                            payloadJson: null,
                                            "operator@localhost.test")))
                                    .ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowDecisionKind.RequestChanges, refusal.StandingDecision);
        AssertEx.Equal(expected: 1, (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "gate.decided"));
    }

    /// <summary>A node run nobody is waiting on has nothing to decide, and saying so is a conflict.</summary>
    [Test]
    public async Task DecidingANodeRunThatIsNotWaiting_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var detail = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false);
        var nodeRunId = detail.NodeRuns.Single().Id;

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                        harness.WithRunServiceAsync(service => service.DecideAsync(detail.Run.Id,
                                            nodeRunId,
                                            Guid.NewGuid(),
                                            DevWorkflowDecisionKind.Approve,
                                            comment: null,
                                            payloadJson: null,
                                            decidedBySubject: null)))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "nothing to decide");
    }

    /// <summary>
    ///     An answer the row's status cannot take is refused at the boundary, so the runtime never has to settle a
    ///     decision it would then have to reject.
    /// </summary>
    [Test]
    public async Task RetryingAnUnansweredGate_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;

        // A gate has no failed attempt to re-run, so Retry is not one of the answers it can take.
        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                              harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                  nodeRunId,
                                  Guid.NewGuid(),
                                  DevWorkflowDecisionKind.Retry,
                                  comment: null,
                                  payloadJson: null,
                                  decidedBySubject: null)))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     X3: a gate takes its three answers, and the interventions belong to a blocked node run. Skipping an OPEN
    ///     gate would be an operator walking past an approval instead of giving one — the one thing a gate exists to
    ///     make impossible, and the property every later slice that puts an apply behind one depends on.
    /// </summary>
    [Test]
    public async Task SkippingAnOpenGate_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var nodeRun = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, nodeRun.Status, "the gate has to be open for this to be the case under test.");

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                        harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                            nodeRun.Id,
                                            Guid.NewGuid(),
                                            DevWorkflowDecisionKind.Skip,
                                            comment: null,
                                            payloadJson: null,
                                            "operator@localhost.test")))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "cannot be answered Skip");
        AssertEx.Equal(expected: 0,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "gate.decided"),
            "a refused answer is not recorded — there is no decision row for the runtime to settle later.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "and the gate is left exactly where it was.");
    }

    /// <summary>
    ///     An intervention answers a blocked node run, and the run settles around it.
    ///     <para>
    ///         RE-PINNED, ruling 1 (Slice D): <c>GateOnly</c>'s single gate IS the graph's terminal node, so skipping
    ///         it walks away from the only thing the run had to do. That ends <c>Cancelled</c>, not <c>Completed</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SkippingABlockedNodeRun_SettlesTheRun()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Blocked).ConfigureAwait(false);

        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;
        var detail = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                      nodeRunId,
                                      Guid.NewGuid(),
                                      DevWorkflowDecisionKind.Skip,
                                      comment: null,
                                      payloadJson: null,
                                      "operator@localhost.test"))
                                  .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, detail.Detail.PendingDecisionCount, "the answer is recorded; the runtime has not acted on it yet.");
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A reused operation id naming a different request is a caller bug, not a replay: answering it with the run it
    ///     did create would hand out a run nobody asked for.
    /// </summary>
    [Test]
    public async Task ReplayingAStartWithADifferentWorkItem_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var (otherWorkItemId, _) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        _ = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, operationId)).ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                        harness.WithRunServiceAsync(service => service.StartAsync(otherWorkItemId, definitionId, inputsJson: null, operationId)))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "already started a different run");
    }

    /// <summary>
    ///     Nothing outside the database is destroyed until the delete has COMMITTED. The rows carry the authoritative
    ///     live-run guard, so a session released ahead of them could be a transcript destroyed for a delete that is
    ///     then refused — the one ordering this method is not allowed to get wrong.
    /// </summary>
    [Test]
    public async Task DeletingAWorkItem_ReleasesItsSessionsOnlyAfterTheRowsAreGone()
    {
        // A private host: OnDeleting is a switch on the container's single fake agent, so setting it would fire
        // on a sibling's deletes too.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var workItemId = (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Id;

        bool? rowsGoneWhenReleased = null;
        harness.Agent.OnDeleting = async _ => rowsGoneWhenReleased = !await harness.WorkItemExistsAsync(workItemId).ConfigureAwait(false);

        await harness.WithRunServiceAsync(service => service.DeleteWorkItemAsync(workItemId)).ConfigureAwait(false);

        AssertEx.True(rowsGoneWhenReleased is true,
            "the session was released while the work item still existed, so a delete refused a moment later would already have destroyed it.");
        AssertEx.True(harness.Agent.Calls.Any(call => call.Verb == "delete" && call.SessionId == sessionId),
            "and the session the run owned was released, rather than left behind with nothing pointing at it.");
    }

    /// <summary>A refused delete destroys nothing: the run is still live, so its transcript and its rows both stay.</summary>
    [Test]
    public async Task DeletingAWorkItemWithALiveRun_IsRefusedAndReleasesNothing()
    {
        // A private host: "no delete happened at all" is an assertion about every call the shared fake recorded.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);
        var workItemId = (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Id;

        _ = await AssertEx.ThrowsAsync<DevWorkflowRunInFlightException>(() => harness.WithRunServiceAsync(service => service.DeleteWorkItemAsync(workItemId)))
                          .ConfigureAwait(false);

        AssertEx.False(harness.Agent.Calls.Any(static call => call.Verb == "delete"), "a refusal must not have released the live run's work session.");
        AssertEx.True(await harness.WorkItemExistsAsync(workItemId).ConfigureAwait(false));
        AssertEx.Equal(sessionId, await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false), "and the node run still owns it.");
    }

    /// <summary>
    ///     A human Retry overrides the NODE's attempt cap — that is what makes it an override — but not the run-wide
    ///     budget. Exhausted, it is refused BEFORE anything is recorded, and that ordering is what leaves the operator
    ///     the other answers: one decision per attempt means a Retry written down and refused afterwards would be the
    ///     last thing that node run could ever be told.
    /// </summary>
    [Test]
    public async Task RetryingPastTheRunWideAttemptBudget_IsRefusedAndLeavesTheOtherAnswersOpen()
    {
        // A private host: the run-wide attempt budget is pinned for this test alone.
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // The first Retry spends the run's one re-attempt, which is what the second is then judged against.
        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Blocked).ConfigureAwait(false);
        var blocked = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;
        _ = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                             blocked,
                             Guid.NewGuid(),
                             DevWorkflowDecisionKind.Retry,
                             comment: null,
                             payloadJson: null,
                             "operator@localhost.test"))
                         .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Attempt,
            "the re-attempt landed, so the run's budget of one is now spent.");

        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Blocked).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                        harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                            nodeRunId,
                                            Guid.NewGuid(),
                                            DevWorkflowDecisionKind.Retry,
                                            comment: null,
                                            payloadJson: null,
                                            "operator@localhost.test")))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message,
            "as many re-attempts as this run allows",
            message: "this copy ships verbatim to the intervention panel, so it has to name the RUN's budget — the node's own cap is what a human Retry overrides.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "the node run is left exactly where it was.");

        // And the other interventions still work, which is the whole reason the refusal comes before the record.
        // RE-PINNED, ruling 1 (Slice D): skipping the gate settles the run Cancelled — it is this graph's only end.
        _ = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                             nodeRunId,
                             Guid.NewGuid(),
                             DevWorkflowDecisionKind.Skip,
                             comment: null,
                             payloadJson: null,
                             "operator@localhost.test"))
                         .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A cancel whose answer the caller never saw is retried against a run the dispatcher has since drained. It is
    ///     a replay of a command that already did exactly what was asked, so it answers with the run — judging it
    ///     against the terminal status its own first attempt produced would refuse a caller for doing the right thing.
    /// </summary>
    [Test]
    public async Task ReplayingACancelAfterTheRunHasDrained_AnswersWithTheRunRatherThanAConflict()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        _ = await harness.WithRunServiceAsync(service => service.CancelAsync(runId, operationId)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "the drain has finished by the time the retry arrives, which is what used to make it a conflict.");

        // Counted rather than named: the ASK and the settled terminal both record run.cancelled, so what a replay must
        // not do is add to the trail at all.
        var before = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count;

        var replay = await harness.WithRunServiceAsync(service => service.CancelAsync(runId, operationId)).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, replay.Run.Status);
        AssertEx.Equal(before, (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count, "and the replay wrote nothing.");
    }

    /// <summary>
    ///     The same for a resume, which carries a status check of its own — so the replay has to be resolved ahead of
    ///     that check too, not only ahead of the transition table.
    /// </summary>
    [Test]
    public async Task ReplayingAResumeAfterTheRunIsRunningAgain_AnswersWithTheRunRatherThanAConflict()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.WithRunServiceAsync(service => service.PauseAsync(runId, Guid.NewGuid())).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        _ = await harness.WithRunServiceAsync(service => service.ResumeAsync(runId, operationId)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var replay = await harness.WithRunServiceAsync(service => service.ResumeAsync(runId, operationId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "run.resumed"),
            "the replay resumed nothing a second time.");
        AssertEx.True(replay.Run.Status is DevWorkflowRunStatus.Running or DevWorkflowRunStatus.WaitingForApproval,
            "and it answered with where the run actually stands.");
    }

    /// <summary>
    ///     An operation id names one ACT, so a lifecycle replay is a replay of its OWN verb or it is nothing. Reused
    ///     across verbs it used to answer success for a cancel that never happened, while the run carried on.
    /// </summary>
    [Test]
    public async Task ReusingALifecycleOperationIdOnADifferentVerb_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        _ = await harness.WithRunServiceAsync(service => service.PauseAsync(runId, operationId)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                        harness.WithRunServiceAsync(service => service.CancelAsync(runId, operationId)))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "run.paused", message: "the refusal says what that operation id actually did.");
        AssertEx.Equal(DevWorkflowRunStatus.Paused,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "and nothing was cancelled — the failure this closes reported success while the run stood still.");
    }

    /// <summary>
    ///     A replay has to be a replay of the SAME act. A reused operation id asking for something else is a caller
    ///     bug, and answering it with the recorded decision would report success for a decision nobody took — in the
    ///     one table that exists to say who decided what.
    /// </summary>
    [Test]
    public async Task ReusingADecisionOperationIdForADifferentAct_IsRefused()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id;
        var operationId = Guid.NewGuid();

        _ = await harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                             nodeRunId,
                             operationId,
                             DevWorkflowDecisionKind.Approve,
                             comment: null,
                             payloadJson: null,
                             "operator@localhost.test"))
                         .ConfigureAwait(false);

        var differentAnswer = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                                harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                                    nodeRunId,
                                                    operationId,
                                                    DevWorkflowDecisionKind.Reject,
                                                    comment: null,
                                                    payloadJson: null,
                                                    "operator@localhost.test")))
                                            .ConfigureAwait(false);
        AssertEx.Contains(differentAnswer.Message, "already recorded a different decision");

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                  harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                                      nodeRunId,
                                      operationId,
                                      DevWorkflowDecisionKind.Approve,
                                      comment: null,
                                      payloadJson: null,
                                      "someone-else@localhost.test")),
                              "and the same answer attributed to a different person is a different act too.")
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "gate.decided"),
            "neither refusal recorded anything.");
    }

    /// <summary>
    ///     The list page and the detail page must not disagree about the same run. Both answer "who is a human holding
    ///     up" from the same rule — a gate awaiting its answer OR a node awaiting intervention, since Blocked folds in.
    /// </summary>
    [Test]
    public async Task TheDetailAndTheListRow_NameTheSameBlockingNodeRun()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Blocked, not WaitingForApproval: the narrower reading of "blocking" answered null here while the list row
        // named the row, which is the disagreement this pins shut.
        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Blocked).ConfigureAwait(false);

        var detail = await harness.WithRunServiceAsync(service => service.GetAsync(runId)).ConfigureAwait(false);
        var row = await harness.ReadWorkItemRowAsync(workItemId).ConfigureAwait(false);

        AssertEx.Equal(row.LatestRunNodes.PendingDecisionCount, detail.PendingDecisionCount);
        AssertEx.Equal(row.LatestRunNodes.BlockingGateNodeRunId, detail.BlockingGateNodeRunId);
        AssertEx.Equal((await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id, detail.BlockingGateNodeRunId);
    }

    /// <summary>The composed detail answers "what is this run waiting on" without a caller re-deriving it.</summary>
    [Test]
    public async Task TheComposedDetail_NamesWhatTheRunIsWaitingOn()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var detail = await harness.WithRunServiceAsync(service => service.GetAsync(runId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, detail.PendingDecisionCount);
        AssertEx.Equal((await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id, detail.BlockingGateNodeRunId);
    }

    /// <summary>
    ///     The cleanup after a delete runs on its own token, and each item on its own. By the time it starts the rows
    ///     have committed, so a caller walking away cannot undo the delete — it can only abandon the sessions and
    ///     artifact bytes that delete has orphaned, and nothing collects those afterwards: the startup sweep takes only
    ///     never-driven sessions, and no row points at these bytes any more. Substituted rather than driven through the
    ///     harness because the window being pinned is one instant wide — between the store's commit and the first
    ///     release — and only a fake can cancel inside it.
    /// </summary>
    [Test]
    public async Task DeletingAWorkItem_ReleasesEverythingEvenWhenTheRequestIsCancelledAtTheCommit()
    {
        using var request = new CancellationTokenSource();
        var workItemId = Guid.NewGuid();
        var sessionIds = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        var runIds = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid()
        };

        var store = Substitute.For<IDevWorkflowStore>();
        _ = store.GetWorkItemAsync(workItemId, Arg.Any<CancellationToken>())
                 .Returns(new DevWorkflowWorkItemSnapshot(workItemId,
                     "Ship the thing",
                     "Please ship it",
                     DevWorkflowWorkItemStatus.Completed,
                     DevelopmentProjectId: null,
                     LatestRunId: null,
                     LatestRunStatus: null,
                     LatestRunDefinitionName: null,
                     DevWorkflowNodeCounters.Empty,
                     CreatedAtUtc: 1,
                     UpdatedAtUtc: 2,
                     Version: 1));

        // The caller walks away in the one instant the finding is about: the rows are committed and nothing external
        // has been released yet.
        _ = store.DeleteWorkItemAsync(workItemId, Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     request.Cancel();
                     return new DevWorkflowWorkItemDeletion(RemovedRows: 6, runIds, sessionIds);
                 });

        // The middle session is refused as well, because one item's failure must cost only itself.
        var sessions = new RecordingWorkSessionLifecycle(sessionIds[1]);

        var swept = new List<Guid>();
        var blobs = Substitute.For<IDevWorkflowArtifactBlobStore>();
        blobs.When(blob => blob.DeleteRun(Arg.Any<Guid>())).Do(call => swept.Add(call.Arg<Guid>()));

        var service = new DevWorkflowRunService(store,
            new NoOpDispatcherSignal(),
            sessions,
            blobs,
            Options.Create(new DevWorkflowOptions()),
            NullLogger<DevWorkflowRunService>.Instance);

        await service.DeleteWorkItemAsync(workItemId, request.Token).ConfigureAwait(false);

        AssertEx.Equal(string.Join(", ", new[]
            {
                sessionIds[0],
                sessionIds[2]
            }),
            string.Join(", ", sessions.Deleted),
            "Every session the delete orphaned has to be released, past the cancellation and past the one that was refused.");
        AssertEx.Equal(string.Join(", ", runIds), string.Join(", ", swept), "And so does every run's artifact directory, which nothing else will ever collect.");
    }

    /// <summary>
    ///     The owner surface, honouring the token it is handed and refusing one nominated session. Hand-written rather
    ///     than substituted because the interface is internal to the Application assembly, which is not exposed to
    ///     Castle's proxy generator.
    /// </summary>
    private sealed class RecordingWorkSessionLifecycle(Guid refused) : IWorkflowOwnedWorkSessionLifecycle
    {
        public List<Guid> Deleted { get; } = [];

        public bool HasCapacity => true;

        public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessionId == refused)
            {
                throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
            }

            Deleted.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<WorkSessionDetail> CreateAsync(string title,
            string objective,
            Guid agentDefinitionId,
            WorkSessionRuntimeOverride? runtime = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkSessionDetail> StartAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkSessionDetail> ResumeAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    ///     The attempt and status every refusal above was judged against travel ON the command, so the store can
    ///     re-check them inside the transaction that writes the decision. Everything the service validates is read
    ///     outside that transaction and under <c>ExpectedVersion.Any</c>, so passing nothing here would leave the
    ///     store's own guard switched off while every other test still passed.
    /// </summary>
    [Test]
    public async Task DecidingANodeRun_TellsTheStoreWhichAttemptTheAnswerWasJudgedAgainst()
    {
        var runId = Guid.NewGuid();
        var nodeRunId = Guid.NewGuid();
        var blocked = BlockedNodeRun(runId, nodeRunId);
        var store = Substitute.For<IDevWorkflowStore>();
        _ = store.GetRunAsync(runId, Arg.Any<CancellationToken>()).Returns(RunAt(runId));
        _ = store.FindDecisionByOperationAsync(runId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DevWorkflowDecisionSnapshot?)null);
        _ = store.GetNodeRunAsync(nodeRunId, Arg.Any<CancellationToken>()).Returns(blocked);
        _ = store.ListNodeRunsAsync(runId, Arg.Any<CancellationToken>()).Returns([blocked]);

        // The write is the last thing this exercises: throwing out of it captures the command without a compose pass
        // over rows no substitute has.
        RecordDevWorkflowDecisionCommand? written = null;
        _ = store.RecordDecisionAsync(Arg.Any<RecordDevWorkflowDecisionCommand>(), Arg.Any<CancellationToken>())
                 .Returns<DevWorkflowMutationResult>(call =>
                 {
                     written = call.Arg<RecordDevWorkflowDecisionCommand>();
                     throw new DevWorkflowNotFoundException("Stopped at the write.");
                 });

        var service = new DevWorkflowRunService(store,
            new NoOpDispatcherSignal(),
            new RecordingWorkSessionLifecycle(Guid.Empty),
            Substitute.For<IDevWorkflowArtifactBlobStore>(),
            Options.Create(new DevWorkflowOptions()),
            NullLogger<DevWorkflowRunService>.Instance);

        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() => service.DecideAsync(runId,
                              nodeRunId,
                              Guid.NewGuid(),
                              DevWorkflowDecisionKind.Retry,
                              "Try it again.",
                              payloadJson: null,
                              "operator@localhost.test"))
                          .ConfigureAwait(false);

        var command = AssertEx.NotNull(written);
        AssertEx.Equal(blocked.Attempt, command.ExpectedAttempt, "the answer names the attempt it was judged against, or the store cannot tell a moved row from a fresh one.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, command.ExpectedStatus);
    }

    /// <summary>A run the substituted store answers with, carrying only what <c>DecideAsync</c> reads off it.</summary>
    private static DevWorkflowRunSnapshot RunAt(Guid runId) =>
        new(runId,
            WorkItemId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            DefinitionGraphHash: "hash",
            GateOnly,
            GraphRevision: 0,
            DevWorkflowRunStatus.Running,
            LastSequence: 3,
            FailureClass: null,
            TerminalReason: null,
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 2);

    /// <summary>A node run standing where an operator Retry is legal: blocked, on an attempt that has been spent.</summary>
    private static DevWorkflowNodeRunSnapshot BlockedNodeRun(Guid runId, Guid nodeRunId) =>
        new(nodeRunId,
            runId,
            "implement",
            DevWorkflowNodeType.DevTask,
            Attempt: 3,
            MaxAttempts: 3,
            SessionResumes: 0,
            DevWorkflowNodeRunStatus.Blocked,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 2,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 1);

    private sealed class NoOpDispatcherSignal : IDevWorkflowDispatcherSignal
    {
        public void Signal(Guid runId)
        {
            // The delete path signals nothing; this exists only to fill the constructor.
        }
    }
}
