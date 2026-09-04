namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
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

    /// <summary>The same one node, pinned to a model and an effort the bound agent definition does not name.</summary>
    private const string SingleAgentPinned = """
                                             {
                                               "schemaVersion": 1,
                                               "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                           "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                           "modelProfile": "qwen3-30b", "reasoningEffort": "high" }],
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

    /// <summary>The agent every graph here binds, so a test is about the lane rather than about routing.</summary>
    private const string SeededAgentId = "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b";

    /// <summary>
    ///     A branch that produces beside one that cannot run at all, both handed to the same <c>All</c> join and one
    ///     agent behind it. The C1 shape reduced to the lane: what reaches the node after the join when a person
    ///     excused one of the branches feeding it.
    /// </summary>
    private const string TwoBranchesIntoAVerification = $$"""
                                                          {
                                                            "schemaVersion": 1,
                                                            "nodes": [
                                                              { "nodeKey": "split", "nodeType": "Parallel", "label": "Split" },
                                                              { "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                                "agentDefinitionId": "{{SeededAgentId}}" },
                                                              { "nodeKey": "doomed", "nodeType": "Agent", "label": "Doomed" },
                                                              { "nodeKey": "join", "nodeType": "Join", "label": "Join" },
                                                              { "nodeKey": "verify", "nodeType": "Agent", "label": "Verify",
                                                                "agentDefinitionId": "{{SeededAgentId}}" }
                                                            ],
                                                            "edges": [
                                                              { "from": "split", "to": "research" },
                                                              { "from": "split", "to": "doomed" },
                                                              { "from": "research", "to": "join" },
                                                              { "from": "doomed", "to": "join" },
                                                              { "from": "join", "to": "verify" }
                                                            ]
                                                          }
                                                          """;

    [ClassDataSource<DevWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required DevWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     A fan of <paramref name="width" /> agent nodes feeding ONE agent node directly — no join, because a join
    ///     produces no artifacts of its own and the node under test has to inherit from every branch at once.
    /// </summary>
    private static string FanInToPlan(int width)
    {
        var research = string.Join(", ",
            Enumerable.Range(1, width)
                      .Select(n => $$"""{ "nodeKey": "r{{n}}", "nodeType": "Agent", "label": "Research {{n}}", "agentDefinitionId": "{{SeededAgentId}}" }"""));
        var edges = string.Join(", ",
            Enumerable.Range(1, width).Select(n => $$"""{ "from": "fan", "to": "r{{n}}" }, { "from": "r{{n}}", "to": "plan" }"""));
        return $$"""
                 {
                   "schemaVersion": 1,
                   "nodes": [ { "nodeKey": "fan", "nodeType": "Parallel" }, {{research}},
                              { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan", "agentDefinitionId": "{{SeededAgentId}}" } ],
                   "edges": [ {{edges}} ]
                 }
                 """;
    }

    /// <summary>Research handing off to a plan node whose own instructions are <paramref name="instructions" />.</summary>
    private static string ResearchThenPlanInstructed(string instructions) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "nodeKey": "research", "nodeType": "Agent", "label": "Research", "agentDefinitionId": "{{SeededAgentId}}" },
              { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan", "agentDefinitionId": "{{SeededAgentId}}",
                "instructions": "{{instructions}}" }
            ],
            "edges": [{ "from": "research", "to": "plan" }]
          }
          """;

    /// <summary>
    ///     Saves an artifact the harness helper cannot: its <c>mediaType</c> is the whole point, and that helper always
    ///     declares <c>text/markdown</c>.
    /// </summary>
    private static async Task SaveBinaryArtifactAsync(DevWorkflowHarness harness, Guid runId, string nodeKey, string name)
    {
        var sessionId = await harness.ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false);
        var artifactId = Guid.NewGuid();
        await using var scope = harness.Services.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<IWorkSessionArtifactBlobStore>()
                                 .WriteAsync(sessionId, artifactId, new byte[]
                                 {
                                     0x89,
                                     0x50,
                                     0x4E,
                                     0x47,
                                     0x0D,
                                     0x0A,
                                     0x1A,
                                     0x0A
                                 })
                                 .ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendArtifactAsync(new AppendWorkSessionArtifactCommand(sessionId,
                           artifactId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionArtifactKind.Report,
                           name,
                           "application/octet-stream",
                           written.ContentHash,
                           written.ByteCount,
                           written.OpaqueReference))
                       .ConfigureAwait(false);
    }

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
    ///     FU-5: a node's authored model and effort reach the session, on the create AND on the start that follows it.
    ///     <para>
    ///         Both, because neither alone would run the node on them: the create is what the tool gate judges, and the
    ///         start is what the step loop reads. A node that pins nothing hands null, which is what leaves the bound
    ///         agent's own configuration in charge.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnAgentNodeWithAModelAndEffort_PinsThemOnItsSessionForEveryDrive()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgentPinned).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("create=qwen3-30b/high, start=qwen3-30b/high",
            string.Join(", ", harness.Agent.Runtimes.Select(entry => $"{entry.Verb}={entry.Runtime?.ModelProfile}/{entry.Runtime?.ReasoningEffort}")),
            "the node's pins travel on the create the tool gate judges and on the start the step loop reads.");
    }

    [Test]
    public async Task AnAgentNodeWithNeitherPin_LeavesTheSessionOnTheBoundAgentsOwn()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("create=, start=",
            string.Join(", ", harness.Agent.Runtimes.Select(entry => $"{entry.Verb}={entry.Runtime?.ModelProfile}")),
            "no override at all, rather than one naming whatever the agent already runs on.");
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

    /// <summary>
    ///     The B3 gate on the agent lane: a provider failure is retryable, so the node run tries again on a NEW work
    ///     session — resuming the one that just failed would resume the context that failed with it — and only a spent
    ///     attempt cap sends it to a human.
    /// </summary>
    [Test]
    public async Task AnAgentNode_WhoseSessionFails_ReAttemptsOnANewSessionUntilItsCapIsSpent()
    {
        // A host of its own: this reads the fake agent's Objectives list by position, and on the shared host that list
        // accumulates every sibling's traffic.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var first = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, retried.Status, "a provider failure with attempts left is re-attempted, not settled.");
        AssertEx.Equal(expected: 2, retried.Attempt);
        AssertEx.NotEqual(first,
            await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false),
            "a retry drives a NEW session; the failed one keeps its transcript as evidence.");
        var trail = await harness.ReadEventTrailAsync(runId).ConfigureAwait(false);
        AssertEx.Contains(trail, "node.retry.scheduled");

        // A new session is only half of it: composed from the same inputs it would be handed a byte-identical
        // objective and do the same thing again, so the attempt that failed travels into the one that follows.
        var objectives = harness.Agent.Objectives;
        AssertEx.NotEqual(objectives[0], objectives[1], "a re-attempt must not be asked for exactly what the attempt before it was asked for.");
        AssertEx.Contains(objectives[1], "priorFailure");
        AssertEx.Contains(objectives[1], "ProviderError");

        var scheduled = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Last(static entry => entry.EventType == "node.retry.scheduled");
        AssertEx.Contains(AssertEx.NotNull(scheduled.DetailJson),
            "\"attempt\":1",
            message: "the event names the attempt that FAILED, which the row no longer carries.");
        AssertEx.Contains(AssertEx.NotNull(scheduled.DetailJson), "ProviderError");

        // Two more failures spend the node's three attempts, and the third has nowhere left to go.
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var exhausted = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, exhausted.Status);
        AssertEx.Equal(expected: 3, exhausted.Attempt);
        AssertEx.Equal(DevWorkflowFailureClasses.ProviderError, exhausted.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(exhausted.OutputJson), "\"failureClass\":\"ProviderError\"");

        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a run waiting on a person needs attention; it is not done.");
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
    ///     A node whose template expands into the implementation lane is told what the coder its tasks become can and
    ///     cannot do, and a node that does not decompose is not. The gating is the half worth pinning: the paragraph is
    ///     about the implementation lane, so appending it to every agent objective would spend the budget of nodes that
    ///     never write a task on rules they cannot break — and the reason it exists at all is that four live runs died
    ///     on slices with nothing to change.
    /// </summary>
    [Test]
    public async Task TheObjective_ForANodeThatDecomposesIntoDevTasks_CarriesWhatEachTaskBecomes()
    {
        // Two private hosts: each asserts on Objectives.Single(), which is a claim about every objective its fake was
        // handed, and the shared host would let one graph's node answer the other's assertion.
        await using var decomposing = new DevWorkflowHarness();
        var decomposingRun = await decomposing.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasks, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
        _ = await decomposing.AdvanceUntilQuiescentAsync(decomposingRun).ConfigureAwait(false);

        await using var plain = new DevWorkflowHarness();
        var plainRun = await plain.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await plain.AdvanceUntilQuiescentAsync(plainRun).ConfigureAwait(false);

        AssertEx.Contains(decomposing.Agent.Objectives.Single(),
            "must finish by submitting a NON-EMPTY code change",
            message: "the decomposing node is told the one rule every slice it writes has to satisfy.");
        AssertEx.Contains(decomposing.Agent.Objectives.Single(),
            "may never modify, delete or rename a test file that already exists",
            message: "and the one an existing-test slice is refused by.");
        AssertEx.False(plain.Agent.Objectives.Single().Contains("What each task becomes", StringComparison.Ordinal),
            $"a node with no materialization writes no tasks and is told nothing about them: {plain.Agent.Objectives.Single()}");
    }

    /// <summary>
    ///     A decomposition whose template subtree carries no <c>DevTask</c> is NOT handed the coder's contract. Its
    ///     clones are ordinary sessions with no patch to export, and the materializer deliberately keeps them that way
    ///     — it refuses a package for naming no changed files only when a DevTask is in the subtree — so telling that
    ///     node every task must produce a code patch and a new test file binds it to rules nothing downstream applies.
    /// </summary>
    [Test]
    public async Task TheObjective_ForADecompositionWithNoDevTaskInItsTemplate_OmitsWhatEachTaskBecomes()
    {
        // A private host for the same reason the pair above uses two: Objectives.Single() is a claim about every
        // objective the fake was handed.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionSubtree, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.False(harness.Agent.Objectives.Single().Contains("What each task becomes", StringComparison.Ordinal),
            $"an Agent-and-Tool template produces no coder attempt, so its decomposition is told nothing about one: {harness.Agent.Objectives.Single()}");
    }

    /// <summary>
    ///     And a template rooted in an Agent with a <c>DevTask</c> BELOW it is handed the contract all the same: the
    ///     coder that cannot finish on an empty patch is one node further down, where reading only the template root
    ///     would miss it. The same whole-subtree read the materializer refuses a package by.
    /// </summary>
    [Test]
    public async Task TheObjective_ForADecompositionWithADevTaskBelowItsTemplateRoot_CarriesWhatEachTaskBecomes()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoAnAgentOverADevTask, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Objectives.Single(),
            "must finish by submitting a NON-EMPTY code change",
            message: "the DevTask under the template root is a coder attempt, so its decomposition is bound by the coder's contract.");
    }

    /// <summary>
    ///     The seeded <c>feature-development-v1</c> decomposition keeps the contract under the new gate: its template
    ///     root <c>implement</c> is the DevTask, so the predicate the executor and the materializer share answers yes.
    ///     Asserted on the predicate rather than on a run, because the seeded graph needs a project, six agents and a
    ///     gate answered to reach its decompose node, and none of that is what this is about.
    /// </summary>
    [Test]
    public void TheSeededFeatureDevelopmentDecomposition_StillMeetsTheContractGate()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph);
        var materialization = AssertEx.NotNull(graph.Nodes["decompose"].Materialization, "the seeded decompose node declares a materialization.");

        AssertEx.True(graph.TemplateSubtreeHasDevTask(materialization),
            "the seeded template expands into a DevTask, so its decomposition is still told what each task becomes.");
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
    ///     The seeded template AS MATERIALIZED: two slices, each an implementation with its own validation behind it,
    ///     both handed to the join beside the decomposition's own edge, and a verification node after it. The clone
    ///     keys are written out rather than produced, because what is under test is what the verification node is
    ///     TOLD, not how the clones came to exist.
    /// </summary>
    private const string MaterializedSlicesIntoAVerification = $$"""
                                                                 {
                                                                   "schemaVersion": 1,
                                                                   "nodes": [
                                                                     { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                                       "agentDefinitionId": "{{SeededAgentId}}" },
                                                                     { "nodeKey": "implement#a", "nodeType": "Agent", "label": "Implement a" },
                                                                     { "nodeKey": "implement#b", "nodeType": "Agent", "label": "Implement b",
                                                                       "agentDefinitionId": "{{SeededAgentId}}" },
                                                                     { "nodeKey": "validate#a", "nodeType": "Tool", "label": "Validate a" },
                                                                     { "nodeKey": "validate#b", "nodeType": "Tool", "label": "Validate b" },
                                                                     { "nodeKey": "join", "nodeType": "Join", "label": "Join" },
                                                                     { "nodeKey": "verify", "nodeType": "Agent", "label": "Verify",
                                                                       "agentDefinitionId": "{{SeededAgentId}}" }
                                                                   ],
                                                                   "edges": [
                                                                     { "from": "decompose", "to": "implement#a" },
                                                                     { "from": "decompose", "to": "implement#b" },
                                                                     { "from": "decompose", "to": "join" },
                                                                     { "from": "implement#a", "to": "validate#a" },
                                                                     { "from": "implement#b", "to": "validate#b" },
                                                                     { "from": "validate#a", "to": "join" },
                                                                     { "from": "validate#b", "to": "join" },
                                                                     { "from": "join", "to": "verify" }
                                                                   ]
                                                                 }
                                                                 """;

    /// <summary>
    ///     The operator's sentence has to survive the clone between them. A person skips the IMPLEMENTATION, its
    ///     validation is skipped behind it, and the validation is what the verification node's producing-ancestor walk
    ///     stops at — so without the reason being propagated the agent asked to judge the run is told only that
    ///     something before it was skipped, and never why.
    /// </summary>
    [Test]
    public async Task TheObjective_CarriesTheOperatorsReasonThroughTheCloneThatWasSkippedBehindIt()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the LAST one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(MaterializedSlicesIntoAVerification).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", """{"tasks":[]}""").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked,
            (await harness.ReadNodeRunAsync(runId, "implement#a").ConfigureAwait(false)).Status,
            "the slice bound to no agent stands down for a human, which is what an operator then skips.");

        await harness.SettleAgentAsync(runId, "implement#b").ConfigureAwait(false);
        await harness.DecideAsync(runId, "implement#a", DevWorkflowDecisionKind.Skip, comment: "This slice names a file the repository does not have.")
                     .ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("Skipped: upstream 'implement#a' was skipped by an operator: This slice names a file the repository does not have.",
            AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "validate#a").ConfigureAwait(false)).TerminalReason),
            "the clone skipped behind the decision quotes it rather than restating it generically.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status);
        AssertEx.Contains(harness.Agent.Objectives[^1],
            "- 'validate#a' was skipped: Skipped: upstream 'implement#a' was skipped by an operator: "
            + "This slice names a file the repository does not have.",
            message: "and the verification node is handed the operator's own sentence, two nodes from where it was written.");
    }

    /// <summary>
    ///     A step a person excused reaches the node after the join BY NAME, with the operator's own reason. Now that an
    ///     <c>All</c> join carries on past a skipped branch (C1), the verification node can be handed four slices where
    ///     the fan-out was five wide — and with nothing saying so it would judge the four as if they were the whole job.
    ///     An absence cannot be read; a line can.
    /// </summary>
    [Test]
    public async Task TheObjective_NamesTheStepsAPersonSkipped()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the LAST one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(TwoBranchesIntoAVerification).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", ResearchMarkdown).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        await harness.DecideAsync(runId, "doomed", DevWorkflowDecisionKind.Skip, comment: "No repository binding exists for this branch.")
                     .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status,
            "the join carried the branch that produced, so the verification node ran.");

        var objective = harness.Agent.Objectives[^1];
        AssertEx.Contains(objective, "### Skipped steps");
        AssertEx.Contains(objective,
            "- 'doomed' was skipped: Skipped by an operator: No repository binding exists for this branch.",
            message: "named, with the operator's reason, so the node judges what was produced against what was not.");
        AssertEx.Contains(objective, ResearchMarkdown, message: "and the branch that DID produce is still handed over whole.");
    }

    /// <summary>
    ///     The skipped steps survive a branch that produced more than the objective can hold. The artifact bodies share
    ///     whatever room is left, so before their share was cut by the list's own length a single long document would
    ///     truncate to the ceiling and the lines under it — appended last — would silently not fit. The node would once
    ///     again judge four slices as if they were five, and nothing in the objective would say otherwise.
    /// </summary>
    [Test]
    public async Task TheObjective_WhenAnUpstreamArtifactWouldFillIt_StillNamesTheStepsAPersonSkipped()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the LAST one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(TwoBranchesIntoAVerification).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", new string('a', 20_000)).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        await harness.DecideAsync(runId, "doomed", DevWorkflowDecisionKind.Skip, comment: "No repository binding exists for this branch.")
                     .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status,
            "the node ran: an over-long objective is refused, and the refusal blocks it for a human.");

        var objective = harness.Agent.Objectives[^1];
        AssertEx.Contains(objective, " characters.)", message: "the document that DID arrive is truncated, which is what leaves the list nothing to fit in.");
        AssertEx.Contains(objective, "### Skipped steps");
        AssertEx.Contains(objective,
            "- 'doomed' was skipped: Skipped by an operator: No repository binding exists for this branch.",
            message: "and the skipped branch is still named, because its room was set aside before the bodies apportioned the rest.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling.");
        AssertEx.True(objective.Length > DevWorkflowAgentExecutor.MaxObjectiveCharacters - 500,
            $"the objective came to only {objective.Length} characters, so the artifact never crowded the budget and this test proves nothing.");
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
        AssertEx.Contains(objective, " characters.)", message: "the marker says how much of the document the agent is not seeing.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling this lane holds itself to.");
    }

    /// <summary>
    ///     The ceiling this lane composes to has to stay inside the one the work-session layer actually refuses on.
    ///     That limit is private to <c>WorkSessionService</c>, so the coupling cannot be read — it is asserted here
    ///     against the REAL service, which is the only thing that can answer it. The harness's fake accepts any
    ///     objective, so no other test in this file would notice the two crossing.
    /// </summary>
    [Test]
    public async Task TheObjectiveLimit_IsTheOneTheWorkSessionLayerActuallyEnforces()
    {
        await using var harness = new DevWorkflowHarness(Host);
        await using var scope = harness.Services.CreateAsyncScope();

        // The agent is deliberately one that does not exist: the objective's length is checked BEFORE the agent is
        // resolved, so whatever this refuses on, it must not be the length.
        var sessions = (IWorkflowOwnedWorkSessionLifecycle)scope.ServiceProvider.GetRequiredService<WorkSessionService>();
        string? refusal = null;
        try
        {
            _ = await sessions.CreateAsync("Boundary", new string('o', DevWorkflowAgentExecutor.MaxObjectiveCharacters), Guid.NewGuid()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            refusal = exception.Message;
        }

        AssertEx.False(refusal?.Contains("objective is longer", StringComparison.OrdinalIgnoreCase) == true,
            $"an objective of exactly {DevWorkflowAgentExecutor.MaxObjectiveCharacters} characters was refused for its length: {refusal}");
    }

    /// <summary>
    ///     A node inheriting from six branches at once renders all six and still lands inside the limit. The headers
    ///     alone are the point: they are written outside any artifact's body, so a bound that counted only bodies would
    ///     be passed by the references without a single byte of content being to blame.
    /// </summary>
    [Test]
    public async Task TheObjective_WithManyUpstreamArtifacts_RendersEveryOneAndStaysInsideTheLimit()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the LAST one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(FanInToPlan(width: 6)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        for (var branch = 1; branch <= 6; branch++)
        {
            _ = await harness.SaveAgentArtifactAsync(runId, $"r{branch}", $"research-{branch}.md", new string((char)('a' + branch), 2000))
                             .ConfigureAwait(false);
            await harness.SettleAgentAsync(runId, $"r{branch}").ConfigureAwait(false);
        }

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).Status);

        var objective = harness.Agent.Objectives[^1];
        for (var branch = 1; branch <= 6; branch++)
        {
            AssertEx.Contains(objective, $"research-{branch}.md", message: "every branch the node inherited from is named, however little room each one got.");
        }

        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"six references and their bodies came to {objective.Length} characters, past the ceiling.");
    }

    /// <summary>
    ///     Instructions long enough to leave almost no room still produce a node that RUNS: the artifact is squeezed to
    ///     whatever is left rather than being allowed to push the objective past the limit and block the node run.
    /// </summary>
    [Test]
    public async Task TheObjective_WhenTheNodesOwnInstructionsFillMostOfIt_SqueezesTheArtifactRatherThanOverrunning()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the SECOND one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlanInstructed(new string('i', 6000))).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", new string('a', 2000)).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).Status,
            "the node ran: an over-long objective is refused, and the refusal blocks it for a human.");

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "research.md", message: "the reference still reaches the agent even when the contents barely do.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling.");
    }

    /// <summary>
    ///     An artifact far larger than any objective could hold is never read at all. Reading is what costs — the whole
    ///     blob, plus a UTF-16 copy of it — and no prefix of a document that size was going to ground anything.
    ///     <para>
    ///         That the read did not happen is proved by removing the bytes first: had anything gone looking for them,
    ///         the objective would say they could not be verified rather than that the artifact is too large.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheObjective_ForAnArtifactTooLargeToInject_SaysSoWithoutEverReadingIt()
    {
        // A private host: HasCapacity is a host-wide switch, and this parks the plan node with it.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlan).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", new string('a', 300_000)).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);

        // Promotion and the next node's dispatch share one tick, so the plan node waits at the queue for the tick that
        // promotes — the only window in which the promoted bytes can be taken away again.
        harness.Agent.HasCapacity = false;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var promoted = AssertEx.NotNull((await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).SingleOrDefault());
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IDevWorkflowArtifactBlobStore>().Delete(runId, promoted.Id);
        }

        harness.Agent.HasCapacity = true;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "research.md", message: "the node is still told the artifact exists.");
        AssertEx.Contains(objective, "too large to include here", message: "and why it is holding a reference rather than contents.");
        AssertEx.False(objective.Contains("did not verify", StringComparison.Ordinal),
            "the bytes were gone, so anything that read them would have reported that instead — the size gate ran first.");
        AssertEx.False(objective.Contains("aaaaaaaaaa", StringComparison.Ordinal), "and no content was injected.");
    }

    /// <summary>
    ///     The case the append guard exists for: instructions leave room for the section and its header but not for a
    ///     body, so what is rendered is a marker rather than content — and a marker is characters too. A bound that
    ///     capped only bodies would let the header and the marker together carry the objective past the limit with no
    ///     document content to blame, which is exactly how this used to overrun.
    /// </summary>
    [Test]
    public async Task TheObjective_WhenOnlyAReferenceFits_KeepsItAndStillStopsAtTheLimit()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the SECOND one.
        await using var harness = new DevWorkflowHarness();
        // Sized so the section header and the artifact's own header still fit but nothing is left for a body: the
        // rendered marker plus that header overrun the ceiling, which is the only shape that reaches the guard.
        var runId = await harness.StartRunAsync(ResearchThenPlanInstructed(new string('i', 6820))).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", new string('a', 2000)).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).Status);

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "research.md", message: "the reference is the half worth keeping when the contents cannot fit.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling — the header and the marker were not counted.");
    }

    /// <summary>
    ///     An artifact that is not text is rendered as a reference and a reason — and that reference is counted against
    ///     the limit like any other, because a reference-only line is still characters in the objective.
    /// </summary>
    [Test]
    public async Task TheObjective_ForAnArtifactThatIsNotText_GivesTheReferenceAndSaysWhyTheresNoContent()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the SECOND one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(ResearchThenPlan).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await SaveBinaryArtifactAsync(harness, runId, "research", "diagram.png").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives[1];
        AssertEx.Contains(objective, "diagram.png", message: "the node is told the artifact exists.");
        AssertEx.Contains(objective, "not text", message: "and why it is holding a reference rather than contents.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling.");
    }

    /// <summary>
    ///     Truncation cuts on a whole character. The budget counts UTF-16 code units and an astral character is two of
    ///     them, so a naive cut can keep half a surrogate pair — which survives in memory and becomes U+FFFD the moment
    ///     the objective is written out as UTF-8, handing the agent a corrupted character.
    ///     <para>
    ///         Two documents of the same astral character, one offset by a single ASCII character, put the pairs on
    ///         opposite parities: whichever way the cut falls, one of them is being asked to split a pair.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheObjective_TruncatingAnArtifactOfAstralCharacters_NeverCutsThroughASurrogatePair()
    {
        // A private host: Objectives is the shared fake's whole history, and this asserts on the LAST one.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(FanInToPlan(width: 2)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var astral = string.Concat(Enumerable.Repeat("\U0001F642", 4000));
        _ = await harness.SaveAgentArtifactAsync(runId, "r1", "even.md", astral).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "r2", "odd.md", $"x{astral}").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "r1").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "r2").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives[^1];
        AssertEx.Equal(objective,
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(objective)),
            "an unpaired surrogate comes back from UTF-8 as U+FFFD, so a round trip that changes the text is a cut through a pair.");
        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective was {objective.Length} characters, past the ceiling.");
    }

    /// <summary>
    ///     FU2-3: what the operator typed when they retried the node reaches the objective the retried attempt runs on,
    ///     under a heading of its own. It used to reach the decision row and stop there — the model was handed a
    ///     byte-identical brief and did the same thing again, with the person's correction visible only in the panel.
    /// </summary>
    [Test]
    public async Task AnAgentNodeRetriedByAnOperator_IsToldWhatTheySaid()
    {
        // A host of its own: this reads the fake agent's Objectives list by position.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        for (var failure = 1; failure <= 3; failure++)
        {
            await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry, comment: "Read the llama-server launch args before you answer.")
                     .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives[^1];
        AssertEx.Contains(objective, "## Operator retry");
        AssertEx.Contains(objective, "Read the llama-server launch args before you answer.");
        AssertEx.False(objective.Contains("operatorRetryAttempt", StringComparison.Ordinal),
            "the bookkeeping member is scaffolding, not something to read out to a model.");
    }
}
