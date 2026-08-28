namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The agent lane: what a node run does with the work session it owns, and what it does when the node has no slot
///     to give it.
/// </summary>
public sealed class DevWorkflowAgentExecutorTests
{
    /// <summary>One agent node, so an assertion is about the lane rather than about routing.</summary>
    private const string SingleAgent = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }],
                                         "edges": []
                                       }
                                       """;

    /// <summary>An agent handing its work to a gate — the shape the whole slice ships, minus the middle step.</summary>
    private const string AgentThenGate = """
                                         {
                                           "schemaVersion": 1,
                                           "nodes": [
                                             { "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                               "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                             { "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }
                                           ],
                                           "edges": [{ "from": "research", "to": "approve" }]
                                         }
                                         """;

    /// <summary>
    ///     The Phase A3 gate, first half: a scripted agent completes, and the node run settles on what its session did.
    /// </summary>
    [Test]
    public async Task AnAgentNode_RunsItsWorkSessionAndSucceedsOnIt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var dispatched = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, dispatched.Status);
        AssertEx.True(dispatched.WorkSessionId is not null, "an agent node run owns the session that does its work.");
        AssertEx.Null(dispatched.QueueReason, "a running node run is not waiting for anything.");
        AssertEx.Equal(expected: 1, harness.Agent.Created.Count);

        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, settled.Status);
        AssertEx.Contains(AssertEx.NotNull(settled.OutputJson), "\"sessionStatus\":\"completed\"");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        AssertEx.Equal("run.created, node.materialized, run.started, node.queued, worksession.attached, node.started, node.completed, run.completed",
            await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "the queue hop and the session it was handed are both part of the audit.");
    }

    /// <summary>
    ///     The Phase A3 gate, second half: a refused admission is queueing, not failure. The row says what it is waiting
    ///     for, nothing records an error, and the next tick asks again.
    /// </summary>
    [Test]
    public async Task AnAgentNode_WhenTheNodeHasNoSlot_StaysQueuedWithAReasonAndNoFailure()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Agent.HasCapacity = false;
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var queued = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Queued, queued.Status);
        AssertEx.Equal(DevWorkflowQueueReasons.AwaitingAgentSlot, queued.QueueReason);
        AssertEx.True(queued.QueuedAtUtc is not null, "the UI shows how long the queue has held it.");
        AssertEx.Null(queued.WorkSessionId, "a refused admission must not leave a session nothing is driving.");
        AssertEx.Empty(harness.Agent.Created);

        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Empty(events.Where(static entry => entry.EventType is "node.failed" or "node.intervention.required"),
            "a lane that will not take a node run yet is queueing, and an event saying otherwise would be a lie in the durable log.");
        AssertEx.Equal(DevWorkflowRunStatus.Running, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        // The slot frees, and nothing has to re-derive eligibility: the row is already queued, so the next tick simply
        // asks the lane again.
        harness.Agent.HasCapacity = true;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
    }

    /// <summary>A lost admission race is the same answer as a full node, and must not burn the session it already owns.</summary>
    [Test]
    public async Task AnAgentNode_WhenTheStartLosesTheAdmissionRace_KeepsItsSessionAndStaysQueued()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Agent.RefuseStart = true;
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var queued = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Queued, queued.Status);
        AssertEx.True(queued.WorkSessionId is not null, "the session was created and attached before the start was refused.");
        AssertEx.Equal(expected: 1, harness.Agent.Created.Count);

        harness.Agent.RefuseStart = false;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            harness.Agent.Created.Count,
            "the retry starts the session it already owns rather than stranding a conversation nobody will drive.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
    }

    /// <summary>An agent binding this node cannot use is not retryable, so it asks a human instead of looping.</summary>
    [Test]
    public async Task AnAgentNode_WhoseAgentCannotBeUsed_BlocksForAHumanWithTheReasonVerbatim()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Agent.RefuseCreateWith = "The agent's model cannot call tools.";
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, blocked.FailureClass);
        AssertEx.Equal("The agent's model cannot call tools.", blocked.TerminalReason, "the message already names the fix, so it is surfaced rather than replaced.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a blocked node run blocks its work item in the same transaction, even though the run status never moved.");
    }

    /// <summary>A node run whose agent binds nothing at all cannot be guessed at either.</summary>
    [Test]
    public async Task AnAgentNode_BindingNoAgentAtAll_BlocksForAHuman()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync("""
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [{ "nodeKey": "research", "nodeType": "Agent" }],
                                                  "edges": []
                                                }
                                                """)
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "binds no agent definition");
    }

    /// <summary>
    ///     A session that parks on its own step budget is resumed rather than failed: parking is routine, because a
    ///     workflow node routinely needs more steps than one session run allows.
    /// </summary>
    [Test]
    public async Task AnAgentNode_WhoseSessionParks_ResumesItUntilTheBudgetIsSpent()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxSessionResumesPerNodeRun", "2"));
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        for (var park = 1; park <= 2; park++)
        {
            await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Paused).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

            var resumed = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
            AssertEx.Equal(DevWorkflowNodeRunStatus.Running, resumed.Status, "a parked session is continued, not failed.");
            AssertEx.Equal(park, resumed.SessionResumes);
        }

        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var exhausted = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, exhausted.Status, "a spent budget asks a human; the work so far is on the session.");
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, exhausted.FailureClass);
    }

    /// <summary>A failed session fails its node run, and the run with it once nothing is live.</summary>
    [Test]
    public async Task AnAgentNode_WhoseSessionFails_FailsTheNodeRunAsAProviderError()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var failed = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Failed, failed.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.ProviderError, failed.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(failed.OutputJson), "\"failureClass\":\"ProviderError\"");

        AssertEx.Equal(DevWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a failed run needs attention; it is not done.");
    }

    /// <summary>
    ///     Cancelling a run stops the session rather than abandoning it, and the row settles on what the session
    ///     actually did rather than on what the drain assumed.
    /// </summary>
    [Test]
    public async Task CancellingARun_StopsTheAgentsSessionAndSettlesTheRowOnIt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Calls, call => call == ("cancel", sessionId), "the executor owns what stopping its work costs.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A pause parks the session and collapses the row, and the resume continues THAT session rather than starting
    ///     the work over — which is the whole reason a pause is not a cancel.
    /// </summary>
    [Test]
    public async Task PausingARun_ParksTheAgentsSessionAndResumingContinuesIt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Calls, call => call == ("pause", sessionId));
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var parked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, parked.Status, "a pause is meant to be resumed, so the row waits rather than terminalizing.");
        AssertEx.Equal(sessionId, parked.WorkSessionId, "it keeps the session, which is what makes the resume a continuation.");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Agent.Created.Count, "the resumed node run must not start a second session.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     What the agent produced becomes the run's own artifact, because the session is scratch that can be deleted
    ///     with its node run while the run's artifacts are the audit that outlives it.
    /// </summary>
    [Test]
    public async Task ACompletedAgent_PromotesWhatItsSessionProducedOntoTheRun()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "findings.md", "# What the runtime does").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var promoted = AssertEx.NotNull(artifacts.SingleOrDefault(), "the run carries one artifact of its own.");
        AssertEx.Equal("findings.md", promoted.Name);
        AssertEx.Equal("research", promoted.ProducingNodeKey);
        AssertEx.Equal(expected: 1, promoted.Version);
        AssertEx.True(promoted.IsLatest);
        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).OutputJson), "\"artifactCount\":1");
    }

    /// <summary>
    ///     A gate's evidence list, and the objective the next agent is handed, both come from the same record: what the
    ///     steps before it produced, captured when it was handed them.
    /// </summary>
    [Test]
    public async Task AGateAfterAnAgent_RecordsThatAgentsArtifactsAsItsEvidence()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "plan.md", "1. Read the code").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);

        var promoted = AssertEx.NotNull((await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).SingleOrDefault());
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, consumed.Count);
        AssertEx.Contains(consumed,
            promoted.Id,
            "the gate did consume that artifact, so recording it costs no new field and gives the panel its evidence.");
    }

    /// <summary>
    ///     The operator's request has to reach the agent, and so do the artifacts before it. Asserted on the objective
    ///     the lane actually handed over, because a node whose objective is the template's generic text is a node that
    ///     does not know what was asked.
    /// </summary>
    [Test]
    public async Task TheObjective_CarriesTheRequestAndTheUpstreamArtifacts()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate, "Explain how the inference path works.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Objectives.Single(), "Explain how the inference path works.");
        AssertEx.Contains(harness.Agent.Objectives.Single(), "Research", message: "the node's own label says which step this is.");
    }
}
