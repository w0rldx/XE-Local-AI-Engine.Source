namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
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

    /// <summary>
    ///     A start pins the graph, gives every node a row, seeds the entry rows with what was asked, and tells the
    ///     dispatcher — the last of which is what keeps a fresh run from sitting visibly Pending until the next sweep.
    /// </summary>
    [Test]
    public async Task StartingARun_MaterializesTheGraphSeedsTheRequestAndSignals()
    {
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
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                              harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A graph that runs commands in a repository needs one. Checked at run start rather than at save, because the
    ///     same definition is legitimately reusable by a work item that does name a project.
    /// </summary>
    [Test]
    public async Task StartingARepositoryBoundGraphOnAProjectlessWorkItem_IsRefused()
    {
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync("""
                                                                          {
                                                                            "schemaVersion": 1,
                                                                            "nodes": [{ "nodeKey": "validate", "nodeType": "Tool" }],
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
        await using var harness = new DevWorkflowHarness();
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
        await using var harness = new DevWorkflowHarness();
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
        await using var harness = new DevWorkflowHarness();
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

    /// <summary>A node run nobody is waiting on has nothing to decide, and saying so is a conflict.</summary>
    [Test]
    public async Task DecidingANodeRunThatIsNotWaiting_IsRefused()
    {
        await using var harness = new DevWorkflowHarness();
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
        await using var harness = new DevWorkflowHarness();
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

    /// <summary>An intervention answers a blocked node run, and the run finishes around it.</summary>
    [Test]
    public async Task SkippingABlockedNodeRun_LetsTheRunFinish()
    {
        await using var harness = new DevWorkflowHarness();
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
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>The composed detail answers "what is this run waiting on" without a caller re-deriving it.</summary>
    [Test]
    public async Task TheComposedDetail_NamesWhatTheRunIsWaitingOn()
    {
        await using var harness = new DevWorkflowHarness();
        var (workItemId, definitionId) = await harness.SeedDefinitionAsync(GateOnly).ConfigureAwait(false);
        var runId = (await harness.WithRunServiceAsync(service => service.StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())).ConfigureAwait(false)).Run.Id;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var detail = await harness.WithRunServiceAsync(service => service.GetAsync(runId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, detail.PendingDecisionCount);
        AssertEx.Equal((await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Id, detail.BlockingGateNodeRunId);
    }
}
