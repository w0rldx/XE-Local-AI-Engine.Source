namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
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

    /// <summary>The seeded template's shape: one agent hands the next what it produced, which is the whole contract.</summary>
    private const string ResearchThenPlan = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [
                                                { "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                  "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan",
                                                  "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }
                                              ],
                                              "edges": [{ "from": "research", "to": "plan" }]
                                            }
                                            """;

    /// <summary>What the research node writes, distinctive enough that finding it downstream cannot be a coincidence.</summary>
    private const string ResearchMarkdown = """
                                            # What the inference path does
                                            A request lands on the chat endpoint, the registry resolves a model, and the
                                            provider streams the answer back through the same hub the UI subscribes to.
                                            """;

    [ClassDataSource<DevWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required DevWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     The Phase A3 gate, first half: a scripted agent completes, and the node run settles on what its session did.
    /// </summary>
    [Test]
    public async Task AnAgentNode_RunsItsWorkSessionAndSucceedsOnIt()
    {
        // A private host: Created.Count is the shared fake's whole history, not this run's.
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
    ///     A session whose attach fails is deleted again on the way out.
    ///     <para>
    ///         Between the create and the attach nothing references the session: the next tick creates another, a
    ///         work-item delete can only release what its node runs point at, and the owner surface refuses a
    ///         workflow-kind session to every other caller. Left behind it is unreachable for good.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnAgentNodeWhoseAttachFails_DeletesTheSessionItHadJustCreated()
    {
        // A private host, for the same reason: the window under test is "exactly one session was created".
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        var nodeRun = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<DevWorkflowAgentExecutor>();
        var store = Substitute.For<IDevWorkflowStore>();
        store.ListOwnedWorkSessionIdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AttachWorkSessionAsync(Arg.Any<AttachDevWorkflowWorkSessionCommand>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new DevWorkflowInvalidTransitionException("The attach lost its race."));

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                                  executor.DispatchAsync(store,
                                      DevWorkflowGraph.Parse(SingleAgent),
                                      run,
                                      DevWorkflowGraph.Parse(SingleAgent).Nodes["research"],
                                      nodeRun,
                                      [nodeRun],
                                      CancellationToken.None),
                              "the attach's failure is what the caller sees; the cleanup is not the story.")
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Agent.Created.Count, "a session WAS created — that is the window under test.");
        var created = harness.Agent.Created.Single();
        AssertEx.True(harness.Agent.Calls.Any(call => call.Verb == "delete" && call.SessionId == created),
            "and it was released again, because nothing else will ever be able to find it.");
    }

    /// <summary>
    ///     The Phase A3 gate, second half: a refused admission is queueing, not failure. The row says what it is waiting
    ///     for, nothing records an error, and the next tick asks again.
    /// </summary>
    [Test]
    public async Task AnAgentNode_WhenTheNodeHasNoSlot_StaysQueuedWithAReasonAndNoFailure()
    {
        // A private host: HasCapacity is a switch on the container's single fake agent, so flipping it would
        // stall every concurrent sibling's agent node too.
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
        // A private host: RefuseStart is the same host-wide switch, and Created.Count is the fake's whole history.
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
        // A private host: RefuseCreateWith is the same host-wide switch.
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

    /// <summary>
    ///     The case the work-item write exists for: one branch blocks while a sibling carries on, so the RUN stays
    ///     Running and the end-of-tick recomputation writes nothing. The item still has to say a human is needed —
    ///     surfacing exactly that is the list page's whole job — so the release travels with the node run's own move.
    /// </summary>
    [Test]
    public async Task ANodeBlockingWhileASiblingWorksOn_LeavesTheRunRunningAndTheWorkItemBlocked()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync("""
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [
                                                    { "nodeKey": "fan", "nodeType": "Parallel" },
                                                    { "nodeKey": "unbound", "nodeType": "Agent" },
                                                    { "nodeKey": "bound", "nodeType": "Agent",
                                                      "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                    { "nodeKey": "join", "nodeType": "Join" }
                                                  ],
                                                  "edges": [
                                                    { "from": "fan", "to": "unbound" },
                                                    { "from": "fan", "to": "bound" },
                                                    { "from": "unbound", "to": "join" },
                                                    { "from": "bound", "to": "join" }
                                                  ]
                                                }
                                                """)
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "unbound").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "bound").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Running,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "the run is genuinely still working: a sibling is mid-flight.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "and it still needs a human, which no later run-status move was going to say.");

        // The release travels the same way: answering the blocked node with the run status still unchanged puts the
        // item back to Active without waiting for a run transition that may never come.
        await harness.DecideAsync(runId, "unbound", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Running, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Active, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>A node run whose agent binds nothing at all cannot be guessed at either.</summary>
    [Test]
    public async Task AnAgentNode_BindingNoAgentAtAll_BlocksForAHuman()
    {
        await using var harness = new DevWorkflowHarness(Host);
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
        // A private host: the per-node-run resume budget is pinned for this test alone.
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
        await using var harness = new DevWorkflowHarness(Host);
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
        await using var harness = new DevWorkflowHarness(Host);
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
        // A private host: Created.Count is the shared fake's whole history.
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
        await using var harness = new DevWorkflowHarness(Host);
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
        await using var harness = new DevWorkflowHarness(Host);
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
        // A private host: Objectives.Single() is an assertion about every objective the shared fake was handed.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate, "Explain how the inference path works.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Objectives.Single(), "Explain how the inference path works.");
        AssertEx.Contains(harness.Agent.Objectives.Single(), "Research", message: "the node's own label says which step this is.");
    }

    /// <summary>
    ///     The seeded contract, end to end: the plan node is told to turn research.md into a plan, so it has to be
    ///     handed research.md — the whole of it, promoted onto the run and rendered into the objective. A reference it
    ///     cannot dereference would leave it inventing a plan while the run still reported success.
    /// </summary>
    [Test]
    public async Task TheObjective_CarriesTheContentsOfTheUpstreamArtifactsAndNotJustTheirNames()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the SECOND one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlan).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", ResearchMarkdown).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 2, harness.Agent.Objectives.Count, "the plan node was handed an objective of its own.");

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "research.md", message: "the reference is still there — the audit is what it answers.");
        AssertEx.Contains(objective, ResearchMarkdown, message: "and so are the bytes, which is what the plan node is asked to transform.");
    }

    /// <summary>
    ///     Content is injected only once the blob store has verified the digest the artifact row recorded. Bytes that no
    ///     longer match are named as unverified rather than handed over: an agent cannot tell tampered research from
    ///     real research, so it must not be given the chance to reason about it as if it were.
    /// </summary>
    [Test]
    public async Task TheObjective_WhenAnUpstreamArtifactsBytesDoNotVerify_SaysSoAndInjectsNothing()
    {
        // A private host: HasCapacity is a host-wide switch, and this holds the plan node at the queue with it.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlan).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", ResearchMarkdown).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);

        // The promotion and the next node's dispatch share one tick, so the plan node is parked at the queue for the
        // tick that promotes — which is the only window in which the stored bytes can be replaced underneath it.
        harness.Agent.HasCapacity = false;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var promoted = AssertEx.NotNull((await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).SingleOrDefault());
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            // Removed and rewritten rather than overwritten, because the blob store is write-once and refuses a second
            // write of different bytes — so this is what tampering has to look like: something got at the file itself.
            // The replacement is the same byte COUNT, so only the digest can catch it, which is the check under test.
            var blobs = scope.ServiceProvider.GetRequiredService<IDevWorkflowArtifactBlobStore>();
            blobs.Delete(runId, promoted.Id);
            _ = await blobs.WriteAsync(runId, promoted.Id, Encoding.UTF8.GetBytes(new string('x', ResearchMarkdown.Length))).ConfigureAwait(false);
        }

        harness.Agent.HasCapacity = true;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "research.md", message: "the reference survives: the artifact does exist and the audit still says so.");
        AssertEx.Contains(objective, nameof(DevWorkflowArtifactReadStatus.HashMismatch), message: "and the objective names why its contents are missing.");
        AssertEx.False(objective.Contains("xxxxxxxxxx", StringComparison.Ordinal), "unverified bytes must never reach the agent.");
    }

    /// <summary>
    ///     A document longer than the objective can hold is truncated with a marker, never dropped and never allowed to
    ///     overrun. The work-session layer REFUSES an objective past its 8000-character limit rather than trimming it,
    ///     so an uncapped injection would block the node run for a human instead of running it.
    /// </summary>
    [Test]
    public async Task TheObjective_WhenAnUpstreamArtifactIsLongerThanItCanHold_TruncatesItAndSaysSo()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the SECOND one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlan).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", new string('a', 20_000)).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).Status,
            "the node ran: an over-long objective would have been refused and blocked it for a human.");

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, " of 20000 characters.)", message: "the marker says how much of the document the agent is not seeing.");
        AssertEx.True(objective.Length < 8000, $"the objective was {objective.Length} characters, which the work-session layer would refuse.");
    }
}
