namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The dispatcher over real rows: what one tick decides, and what a run's whole life looks like from the outside.
///     <para>
///         Every advance here is explicit. Nothing sleeps, nothing polls, and the assertions are about the event trail
///         as much as the end state — a runtime whose audit log is the replay authority has to be tested on what it
///         wrote, not only on where it landed.
///     </para>
/// </summary>
public sealed class DevWorkflowDispatcherTests
{
    /// <summary>A human gate on its own: one node, one question, one answer, and the run is done.</summary>
    private const string GateOnly = """
                                    {
                                      "schemaVersion": 1,
                                      "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                      "edges": []
                                    }
                                    """;

    /// <summary>One agent node, so the run stays busy while a test looks at what else the node will admit.</summary>
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
    ///     A run's node runs are written with the run row, so the dispatcher never composes any of its own.
    ///     <para>
    ///         It used to, for a run it found without them — and it had nothing to seed the entry rows WITH, because
    ///         the caller's inputs live nowhere but those rows. A run interrupted between the two commits therefore
    ///         came back as the same graph quietly running a different request. Now the two commits are one, and this
    ///         is the proof of what the dispatcher does with the only shape that could still ask it to guess.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunFoundWithoutNodeRuns_IsNotMaterializedByTheDispatcher()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunWithoutNodeRunsAsync(GateOnly).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Empty(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false),
            "the dispatcher cannot know what this run was asked to do, so it must not invent rows that claim it can.");
        AssertEx.Equal(expected: 0,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.materialized"),
            "and it announced no materialization either.");
    }

    /// <summary>
    ///     The concurrent-run cap is enforced where a Pending run is admitted: over the cap, a run waits rather than
    ///     being refused, and the next tick that finds room starts it.
    /// </summary>
    [Test]
    public async Task ARunOverTheConcurrencyCap_WaitsUntilAnotherFinishes()
    {
        // A private host: the cap counts Running runs across the whole DATABASE, so pinning it to one is a
        // host-level fact a shared sibling's run would break.
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxConcurrentRuns", "1"));
        var first = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(first).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Running, (await harness.ReadRunAsync(first).ConfigureAwait(false)).Status, "the first run holds the node's one slot.");

        var second = await harness.StartRunAsync(SingleAgent, "A second request").ConfigureAwait(false);
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(second).ConfigureAwait(false), "a tick that admits nothing writes nothing.");
        AssertEx.Equal(DevWorkflowRunStatus.Pending,
            (await harness.ReadRunAsync(second).ConfigureAwait(false)).Status,
            "over the cap the run WAITS — refusing it would push a queue the node can work through back onto the person who started it.");

        await harness.SettleAgentAsync(first, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(first).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(first).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceUntilQuiescentAsync(second).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Running,
            (await harness.ReadRunAsync(second).ConfigureAwait(false)).Status,
            "and the slot the finished run gave back is what lets the waiting one start.");
    }

    /// <summary>
    ///     The Phase A2 gate. A human-gate-only definition runs to completion: the run materializes its entry node,
    ///     stops on the human, and finishes on the answer — with the work item tracking it the whole way.
    /// </summary>
    [Test]
    public async Task AHumanGateDefinition_RunsToCompletionOnItsDecision()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Pending, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var waiting = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, waiting.Status);
        AssertEx.Equal(DevWorkflowDecisionKind.Approve, waiting.PendingDecisionKind, "the gate says which answer it expects.");
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a run waiting on a human blocks its work item, and the runtime writes that — no client ever does.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, settled.Status);
        AssertEx.Null(settled.PendingDecisionKind, "the pending marker is cleared when the decision lands.");
        AssertEx.Contains(AssertEx.NotNull(settled.OutputJson), "\"decision\":\"Approve\"");

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Completed, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);

        AssertEx.Equal("run.created, node.materialized, run.started, node.started, gate.requested, run.waiting, gate.decided, node.completed, run.completed",
            await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "the whole run has to be replayable from the log, in order.");
    }

    /// <summary>
    ///     Sequence numbers are the replay contract: a client reading everything after watermark N must be able to trust
    ///     that it will see each row once and in order.
    ///     <para>
    ///         Strictly increasing rather than contiguous, and that distinction is the contract: one counter per run
    ///         serves the events AND the node runs and artifacts, so an event feed legitimately steps over the numbers
    ///         the rows it describes took.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunsEventSequenceIsStrictlyIncreasing()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var sequences = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Select(static entry => entry.Sequence).ToList();

        AssertEx.Equal(expected: 9, sequences.Count);
        AssertEx.Equal(string.Join(", ", sequences.Order()), string.Join(", ", sequences), "the feed arrives in sequence order.");
        AssertEx.Equal(sequences.Count, sequences.Distinct().Count(), "and no two rows ever share a watermark.");
        AssertEx.Equal(sequences[^1], (await harness.ReadRunAsync(runId).ConfigureAwait(false)).LastSequence, "the run's counter is the high-water mark.");
    }

    /// <summary>
    ///     Ticking a settled run is not merely harmless, it is the normal case: the sweep visits every live run and the
    ///     signal channel deliberately drops nothing it cannot afford to re-deliver.
    /// </summary>
    [Test]
    public async Task AdvancingAQuiescentRunWritesNothing()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var trail = await harness.ReadEventTrailAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "a run waiting on a human has nothing for the dispatcher to do.");
        AssertEx.Equal(trail, await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "and it wrote no event either.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "and a completed run is left alone entirely.");
    }

    /// <summary>
    ///     The gate's answer is its output, and routing is the edges' job. A rejection therefore does not fail the gate:
    ///     it takes the reject branch, and the definition decides what that means.
    /// </summary>
    [Test]
    public async Task AGateDecisionRoutesThroughItsOutEdges()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ApprovalBranches).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.RequestChanges).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped,
            (await harness.ReadNodeRunAsync(runId, "ship").ConfigureAwait(false)).Status,
            "the branch whose condition did not match is dead, and its node run says so.");

        // 'revise' is an Agent node, and the agent lane does run those — so the branch being taken is visible in the
        // node run having been admitted at all. It stops on its own binding, which this fixture deliberately leaves empty.
        var revise = await harness.ReadNodeRunAsync(runId, "revise").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, revise.Status);
        AssertEx.Equal("Configuration", revise.FailureClass, "an agent bound to nothing is the definition's gap, and no retry changes the answer.");
        AssertEx.Contains(AssertEx.NotNull(revise.TerminalReason), "binds no agent definition");
    }

    /// <summary>
    ///     A gate answered in a way none of its branches accepts ends the run, and ends it through the drain.
    ///     <para>
    ///         Completing (every downstream skipped) and failing (nothing failed) would each be a lie about a refused
    ///         approval, and writing the terminal directly would strand whatever else the run still had live. Both are
    ///         asserted, because the second is invisible from the end state alone.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AGateAnswerNoBranchAcceptsCancelsTheRunThroughTheDrain()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ApprovalBranches).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Neither out-edge takes 'Reject': one wants Approve, the other RequestChanges.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);

        AssertEx.True(await harness.AdvanceAsync(runId).ConfigureAwait(false) > 0);
        var draining = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelling, draining.Status, "the terminal is reached through the drain, never written over live rows.");
        AssertEx.Equal("GateRejected", draining.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(draining.TerminalReason), "'approve'", message: "the reason names the gate, or nobody can tell which one refused.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "the gate did its job; it is the run that has nowhere left to go.");
    }

    /// <summary>
    ///     A rejection at a terminal gate ends the run, and this is the seeded template's own shape rather than a corner
    ///     case: the last node of "Research → Plan → Approval" has no out-edges, so "no branch accepts the answer" is
    ///     the ONLY way a refused approval can be told apart from an approved one.
    /// </summary>
    [Test]
    [Arguments("Reject")]
    [Arguments("RequestChanges")]
    public async Task ANonApproveAnswerAtATerminalGateCancelsTheRun(string decision)
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "approve", Enum.Parse<DevWorkflowDecisionKind>(decision)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "a refused approval must not read as the run having succeeded.");
        AssertEx.Equal("GateRejected", run.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), decision, message: "the reason names the decision, not just the gate.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>The other half of the same gate: the answer it was waiting for completes it normally.</summary>
    [Test]
    public async Task AnApproveAtATerminalGateCompletesTheRun()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A rejection taken while the run is pausing must survive the pause.
    ///     <para>
    ///         By the time the pause settles the gate is already Succeeded, so nothing would ever re-detect the
    ///         rejection: the run would resume, find nothing live, and COMPLETE — reporting a refused approval as a
    ///         successful run. Only an in-flight cancel may supersede it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARejectionTakenWhilePausingSurvivesTheResume()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "the rejection outranks the pause; resuming into Completed would report a refusal as a success.");
    }

    /// <summary>
    ///     The same rejection-survives-a-pause rule at a gate that HAS branches — so the fix is tested independently of
    ///     the zero-out-edge case, which reaches the cancel by a different arm of the same condition.
    /// </summary>
    [Test]
    public async Task ARejectionTakenWhilePausingSurvivesTheResumeAtABranchingGate()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ApprovalBranches).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);

        // Neither branch takes Reject: one wants Approve, the other RequestChanges.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "the rejection outranks the pause here too.");
        AssertEx.Equal("GateRejected", run.FailureClass);
    }

    /// <summary>
    ///     An Approve no branch accepts strands the run exactly as any other answer does. The zero-out-edge exemption is
    ///     for a gate with nowhere to go by construction — not for an approval that missed every branch it had.
    /// </summary>
    [Test]
    public async Task AnApproveNoBranchAcceptsCancelsTheRunToo()
    {
        await using var harness = new DevWorkflowHarness(Host);

        // Both branches test for something other than Approve, so approving matches neither.
        const string NoApproveBranch = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [
                                           { "nodeKey": "approve", "nodeType": "HumanGate" },
                                           { "nodeKey": "revise", "nodeType": "Gate" },
                                           { "nodeKey": "abandon", "nodeType": "Gate" }
                                         ],
                                         "edges": [
                                           { "from": "approve", "to": "revise", "condition": { "path": "decision", "op": "eq", "value": "RequestChanges" } },
                                           { "from": "approve", "to": "abandon", "condition": { "path": "decision", "op": "eq", "value": "Reject" } }
                                         ]
                                       }
                                       """;

        var runId = await harness.StartRunAsync(NoApproveBranch).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "completing via skipped downstream would be the same lie in the other direction.");
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "Approve");
    }

    /// <summary>
    ///     A decision the node run's status forbids is a durable row re-read on every tick. It must cost that node run
    ///     and nothing else — left to throw, it would wedge the whole run and every healthy sibling with it, forever,
    ///     because the row that throws is still there on the next tick.
    /// </summary>
    [Test]
    public async Task APoisonedDecisionRowCostsItsOwnNodeRunAndNoOther()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TwoStalledSiblings).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "left").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "right").ConfigureAwait(false)).Status);

        // Approve resolves to Succeeded, which a Blocked node run cannot reach. The row is durable and will be re-read.
        await harness.DecideAsync(runId, "left", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked,
            (await harness.ReadNodeRunAsync(runId, "left").ConfigureAwait(false)).Status,
            "the node run keeps the status the decision could not move it out of.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "node.intervention.required");

        // The sibling still answers, on the very ticks the poisoned row is being re-read.
        await harness.DecideAsync(runId, "right", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped,
            (await harness.ReadNodeRunAsync(runId, "right").ConfigureAwait(false)).Status,
            "one poisoned row must not stop the tick that serves every other node run.");
    }

    /// <summary>
    ///     A pause must not be pinned by a node run queued for a slot nothing will hand out while the run drains, so a
    ///     Queued row collapses back to Pending — the same collapse the startup reconciler performs, for the same reason.
    /// </summary>
    [Test]
    public async Task PausingCollapsesAQueuedNodeRunSoItCannotPinTheDrain()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Stand a node run in the state a saturated lane would leave it in. Nothing dispatches it, so without the
        // collapse the drain below would wait on it forever.
        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Queued).ConfigureAwait(false);
        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status, "the drain settled instead of being pinned.");
    }

    /// <summary>
    ///     A sweep must reach EVERY live run, not the newest few.
    ///     <para>
    ///         The page size was the concurrent-run cap, over a list ordered newest-first — so past that cap the oldest
    ///         live runs were never swept again. They are exactly the runs a sweep exists for: a signal is a latency
    ///         hint that can be dropped, and the sweep is the only thing that ever comes back for what was missed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASweepReachesEveryLiveRunAndNotOnlyTheNewest()
    {
        // The cap is raised past the run count deliberately: what is under test is the sweep's PAGE reaching the oldest
        // run, and at the default cap of four the last two would stay Pending for admission reasons instead — a pass
        // for the wrong reason if it went the other way, and a failure that says nothing about paging if it did not.
        // A private host: the cap is raised for this test alone, and SweepAsync visits every run in the database.
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxConcurrentRuns", "8"));

        var runIds = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            runIds.Add(await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate, $"Request {index}").ConfigureAwait(false));
        }

        await harness.SweepAsync().ConfigureAwait(false);

        foreach (var runId in runIds)
        {
            AssertEx.Equal(DevWorkflowRunStatus.Running,
                (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
                "every live run was swept, including the oldest — which the newest-first page used to cut off.");
        }
    }

    /// <summary>
    ///     A productive tick re-signals, so a run advances at graph speed instead of one node per sweep interval.
    ///     <para>
    ///         Driven through a dispatcher the test starts itself, because the test host strips every hosted service —
    ///         so this is the only place the real signal pump runs at all. One signal has to carry the run through
    ///         several ticks; without the re-signal each hop would wait for a sweep, and the interval here is set past
    ///         the test's lifetime so a sweep cannot rescue it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OneSignalCarriesARunThroughEveryTickItStillHasWorkFor()
    {
        // A private host: this starts the real signal and sweep pumps, which would then drive a sibling's runs too.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval).ConfigureAwait(false);

        await using var dispatcher = harness.CreateReplacementDispatcher();
        await dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        dispatcher.Signal(runId);

        _ = await harness.WaitForRunStatusAsync(runId, DevWorkflowRunStatus.WaitingForApproval).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked,
            (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status,
            "one signal reached a node run several ticks past where it was sent.");
    }

    /// <summary>A disabled node registers the dispatcher and starts nothing, so a signal moves no run.</summary>
    [Test]
    public async Task ADisabledNodeStartsNoPump()
    {
        // A private host, for the same reason: it starts a dispatcher, and asserts that nothing moved.
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TerminalGate).ConfigureAwait(false);

        await using var dispatcher = harness.CreateReplacementDispatcher(enabled: false);
        await dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        dispatcher.Signal(runId);

        await AssertEx.SettleAsync().ConfigureAwait(false);

        // The status alone would be true by construction — nothing moved it before the signal either. The signal
        // sitting UNREAD in the channel is what proves the pump never started: a running pump drains it.
        AssertEx.Equal(expected: 1, dispatcher.PendingSignals.Count, "a disabled node must leave the signal unconsumed.");
        AssertEx.Equal(DevWorkflowRunStatus.Pending, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A skip has to reach all the way down, or a run stalls on a node whose upstream will never arrive. Three
    ///     levels, because two would not distinguish propagation from a single hop.
    /// </summary>
    [Test]
    public async Task ASkippedBranchPropagatesToEveryNodeBelowIt()
    {
        await using var harness = new DevWorkflowHarness(Host);

        // The gate node succeeds with no upstream, so its 'passed' condition finds nothing and fails closed — which is
        // exactly the "route on evidence that is not there" case the whole chain below must not take.
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ThreeLevelChain).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("first: Skipped, gate: Succeeded, second: Skipped, third: Skipped",
            string.Join(", ", nodeRuns.OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal).Select(static nodeRun => $"{nodeRun.NodeKey}: {nodeRun.Status}")));

        // RE-PINNED, ruling 1 (Slice D): 'third' is the graph's only terminal node and it was skipped, so the run
        // routed around everything it was asked to do and reached none of it. Completed would read as having done it.
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "a run whose every branch condition was false never reached an end.");
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "'third' was Skipped");
    }

    /// <summary>
    ///     Cancelling is fire-and-forget: the command writes an intent and returns, and the dispatcher settles it. The
    ///     terminal is reached through that drain and never written directly — a terminal written over live node runs
    ///     would strand them under a run no tick ever visits again.
    /// </summary>
    [Test]
    public async Task CancellingDrainsTheLiveNodeRunsBeforeTheRunReachesCancelled()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, gate.Status, "the human wait was live, so the drain settled it.");
        AssertEx.Equal("Cancelled", gate.FailureClass);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A pause is meant to be resumed, so it leaves the human wait alone rather than tearing it down. The run says
    ///     <c>Pausing</c> until it has settled, because the command that asked for it has already returned.
    /// </summary>
    [Test]
    public async Task PausingLeavesAHumanWaitStandingAndResumingPicksItBackUp()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "a durable human wait survives a pause untouched.");

        // The decision is taken while the run is paused, which the dispatcher must defer rather than lose.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "a paused run advances nothing, decision or no decision.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status,
            "the decision was deferred, not lost: the first tick after the resume applied it.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The intervention answers, on a node run this build cannot execute. <c>Skip</c> routes around it, and
    ///     everything below it skips with it.
    ///     <para>
    ///         RE-PINNED, ruling 1 (Slice D): the run then reads <c>Cancelled</c> rather than <c>Completed</c>. A
    ///         skipped node run is terminal and does not block completion, but neither is it an END — this run reached
    ///         none of them, and the work item says the same. Nothing failed, so there is no class to carry either:
    ///         the reason names the end that was abandoned, because a human decision is the whole of the account.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AHumanSkipOnABlockedNodeRunAbandonsTheTailAndCancelsTheRun()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a node needing intervention is a human wait, and the run says so.");

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("approve: Skipped, plan: Skipped, research: Skipped",
            string.Join(", ", nodeRuns.OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal).Select(static nodeRun => $"{nodeRun.NodeKey}: {nodeRun.Status}")));

        var skipped = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, skipped.Status);
        AssertEx.Equal("No terminal node succeeded: 'approve' was Skipped.", AssertEx.NotNull(skipped.TerminalReason));
        AssertEx.Null(skipped.FailureClass, "a Skip is a human decision; there is no failure to classify.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Cancelled, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);

        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("cancelled",
            events.Last(static entry => entry.EventType == "run.cancelled").Outcome,
            "the existing token, for what genuinely is a cancellation — no new event or outcome was minted for this.");
    }

    /// <summary>
    ///     <c>Abandon</c> is the other end of the same decision: the node run fails for good, and the run fails with it —
    ///     which maps the work item to Blocked, because a failed run needs attention rather than being done.
    /// </summary>
    [Test]
    public async Task AHumanAbandonFailsTheNodeRunAndTheRunWithIt()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Abandon).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Failed, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The operator's request has to reach the first node, and there is no run-level input column: every entry node
    ///     run is seeded with it at materialization, which is where the objective composer will read it from.
    /// </summary>
    [Test]
    public async Task TheEntryNodeRunIsSeededWithTheWorkItemsRequest()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ResearchPlanApproval, "Explain the KV cache").ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).InputJson), "Explain the KV cache");

        // Every node run of the graph exists from the start — that is what keeps terminalization honest, since a run
        // with rows still to come would otherwise read as "nothing live" and complete early. Only the ENTRY node is
        // seeded with the request, though: the rest are fed by their upstreams.
        AssertEx.Equal(expected: 3, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count);
        AssertEx.Null((await harness.ReadNodeRunAsync(runId, "plan").ConfigureAwait(false)).InputJson);
    }

    /// <summary>
    ///     A run whose pinned graph cannot be routed fails at its first tick.
    ///     <para>
    ///         The graph is validated again at run start rather than trusted from the save, because an agent definition
    ///         can be deleted in between. Left Pending the run would be swept forever; moved to Running it would claim
    ///         work nothing can do.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunWhosePinnedGraphCannotBeRoutedFailsAtItsFirstTick()
    {
        await using var harness = new DevWorkflowHarness(Host);

        // Two entry nodes: routable JSON, unroutable graph. Nothing validates a definition on the way in yet, so this
        // is exactly the shape a stale or hand-written definition would present at run start.
        var runId = await harness.StartRunAsync("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Gate"},{"nodeKey":"b","nodeType":"Gate"}],"edges":[]}""")
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Failed, run.Status);
        AssertEx.Equal("Configuration", run.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "exactly one entry node");
        AssertEx.Empty(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), "nothing was materialized, so nothing has to be cleaned up.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked,
            (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status,
            "a failed run needs attention rather than being done.");
    }

    /// <summary>
    ///     A run pinned to a graph that declares one edge twice fails the same way, with the sentence saying why.
    ///     <para>
    ///         A CONSEQUENCE of refusing duplicates at parse, and the better of the two failures available: such a
    ///         graph used to parse, and then threw out of the materialization step on a duplicate key — where the
    ///         tick's catch-all logs it and drops it, so every sweep re-threw and the run sat with nothing moving and
    ///         nothing to read. A refusal a person can act on beats a silent wedge.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARunPinnedToAGraphWithADuplicateEdgeFailsWithTheReasonRatherThanWedging()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync("""
                                                {"schemaVersion":1,
                                                 "nodes":[{"nodeKey":"a","nodeType":"Gate"},{"nodeKey":"b","nodeType":"Join"}],
                                                 "edges":[{"from":"a","to":"b"},{"from":"a","to":"b"}]}
                                                """)
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Failed, run.Status, "a graph nothing can route is written down as refused, not retried forever.");
        AssertEx.Equal("Configuration", run.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "twice");
    }

    /// <summary>
    ///     The parsed graph is cached per run and invalidated by the run's revision, so a run that ticks many times
    ///     decrypts and parses its graph exactly once.
    /// </summary>
    [Test]
    public async Task TheParsedGraphIsCachedAcrossTicksAndDroppedWhenTheRunTerminalizes()
    {
        // A private host: ParseCount is the graph cache's own counter, and a sibling parsing would inflate the delta.
        await using var harness = new DevWorkflowHarness();
        var cache = harness.Graphs;
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        var before = cache.ParseCount;
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "several ticks, one parse.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "and the graph never changed, so it never re-parsed.");

        // Terminalizing dropped the entry, and a tick on a terminal run returns before it would need a graph — so the
        // count stays where it was rather than growing on every sweep that visits a finished run.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cache.ParseCount - before, "a terminal run is not parsed at all.");
    }

    /// <summary>
    ///     A restart loses the dispatcher and its cache and nothing else, because the run is entirely in the database.
    ///     Simulated the way the reconciler will be: same rows, fresh dispatcher.
    /// </summary>
    [Test]
    public async Task ARunSurvivesTheDispatcherBeingReplacedMidFlight()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);

        await using (var replacement = harness.CreateReplacementDispatcher())
        {
            _ = await replacement.AdvanceOnceAsync(runId, CancellationToken.None).ConfigureAwait(false);
            _ = await replacement.AdvanceOnceAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a dispatcher that never saw the run's earlier ticks finishes it from the rows alone.");
    }

    /// <summary>
    ///     The automatic gate's truth table: it routes on its upstream's document, records WHICH way it went, and carries
    ///     that document through so its own out-edges and a later reader see the same evidence it decided on.
    ///     <para>
    ///         The branch is what a Gate node buys over a plain conditional edge — the edges would route identically
    ///         without it — so a gate that did not record one would be pure ceremony.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("Approve", "ship", "revise")]
    [Arguments("RequestChanges", "revise", "ship")]
    public async Task AnAutomaticGateRecordsTheBranchItTook(string decision, string taken, string dead)
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.GateOnADecision).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "approve", Enum.Parse<DevWorkflowDecisionKind>(decision)).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "choose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, gate.Status);
        AssertEx.Contains(AssertEx.NotNull(gate.OutputJson), $"\"branch\":\"{taken}\"");
        AssertEx.Contains(AssertEx.NotNull(gate.OutputJson),
            $"\"decision\":\"{decision}\"",
            message: "the upstream document is carried through, so the gate cannot decide one thing and its edges another.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, taken).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, dead).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The third row of the same table: a gate whose conditions all fail records no branch, and everything below it
    ///     skips. That is a real outcome rather than a stranding — the gate itself succeeded, and the log says which way
    ///     it did not go.
    ///     <para>
    ///         RE-PINNED, ruling 1 (Slice D): the run reads <c>Cancelled</c>, and this is the second shape the ruling
    ///         names. The rejection was not stranded — the human gate's own out-edge accepted it, so X10's drain never
    ///         fired — and it still reached no end, because the automatic gate below took neither branch. The gate
    ///         answer is the only account of that, so the run carries <c>GateRejected</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnAutomaticGateWhoseBranchesAllRefuseRecordsNoneOfThem()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.GateOnADecision).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // The human gate's own out-edge is unconditional, so a rejection reaches the automatic gate rather than
        // stranding the run — and neither of THAT gate's branches takes it.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "choose").ConfigureAwait(false)).OutputJson), "\"branch\":null");
        AssertEx.Equal("approve: Succeeded, choose: Succeeded, revise: Skipped, ship: Skipped",
            string.Join(", ",
                (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
                .OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
                .Select(static nodeRun => $"{nodeRun.NodeKey}: {nodeRun.Status}")));

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status);
        AssertEx.Equal("GateRejected", run.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(run.TerminalReason), "after the gate 'approve' answered Reject");
    }

    /// <summary>
    ///     The X10 case as it actually occurs: one branch is mid-build when the other's approval is refused. The run goes
    ///     through the drain, so the sibling settles and gives its lane slot back BEFORE the run reaches its terminal.
    ///     <para>
    ///         Writing the terminal directly would trip the "if terminal, return" guard at the top of every tick, and
    ///         nothing would ever poll that sibling again — its slot would be held for the life of the process.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AGateRejectionDrainsALiveSiblingBeforeTheRunEnds()
    {
        // A private host: this scripts the sandbox seam, which is host-wide.
        await using var harness = new DevWorkflowHarness();
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.GateBesideSandboxWork, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);

        // Only the Approve edge leaves the gate, so a rejection has nowhere to go.
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        AssertEx.True(await harness.AdvanceAsync(runId).ConfigureAwait(false) > 0);

        var draining = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelling, draining.Status, "the terminal is reached through the drain, never written over a live row.");
        AssertEx.Equal("GateRejected", draining.FailureClass);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status,
            "the sibling is still in the sandbox at the moment the rejection lands.");

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var sibling = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, sibling.Status);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        // Filtered to the sibling's own row: the join below it is cancelled by the same drain, and it is the one that
        // was holding a lane slot whose ordering matters.
        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.True(events.Single(entry => entry.EventType == "node.cancelled" && entry.NodeRunId == sibling.Id).Sequence
                      < events.Last(static entry => entry.EventType == "run.cancelled").Sequence,
            "the sibling settles before the run does; the other order leaks its lane slot.");
    }

    /// <summary>
    ///     The C1 gate: an <c>Any</c> join with one dead branch. Two things, and the first is the subtle one.
    ///     <para>
    ///         <c>Any</c> does NOT fire on whichever branch landed first — it waits until no inbound edge is still
    ///         pending, because a sibling that has not settled could still satisfy one, and a join that routed on
    ///         whichever branch happened to be quicker would make the run's path a matter of timing. A branch standing
    ///         down for a human is not settled: <c>Blocked</c> is a wait, not an answer.
    ///     </para>
    ///     <para>
    ///         Once the dead branch does settle, the surviving branch carries the join, and the run ends on what that
    ///         branch became: abandoned is a failure the run reports, skipped is a branch the run routed around. Either
    ///         way the dead branch KEEPS its terminal — a join is not a reason to re-run the node that will never
    ///         arrive, and a resurrected row would be the run asking again a question it has already been answered.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(DevWorkflowDecisionKind.Abandon, DevWorkflowNodeRunStatus.Failed, DevWorkflowRunStatus.Failed)]
    [Arguments(DevWorkflowDecisionKind.Skip, DevWorkflowNodeRunStatus.Skipped, DevWorkflowRunStatus.Completed)]
    public async Task AnAnyJoinWaitsForItsDeadBranchToSettleAndThenCarriesTheSurvivor(DevWorkflowDecisionKind decision,
        DevWorkflowNodeRunStatus deadStatus,
        DevWorkflowRunStatus runStatus)
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.AnyJoinOverADeadBranch, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "anysurvivor").ConfigureAwait(false)).Status);
        var doomed = await harness.ReadNodeRunAsync(runId, "anydoomed").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, doomed.Status, "an agent bound to nothing stands down for a human rather than settling.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "anymerge").ConfigureAwait(false)).Status,
            "one satisfied branch is not enough while a sibling could still satisfy its own edge.");

        await harness.DecideAsync(runId, "anydoomed", decision).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "anydoomed").ConfigureAwait(false);
        AssertEx.Equal(deadStatus, settled.Status);
        AssertEx.Equal(doomed.Attempt, settled.Attempt, "the dead branch is not re-attempted to satisfy the join.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "anymerge").ConfigureAwait(false)).Status,
            "and with nothing left pending, the one satisfied branch carries the join.");
        AssertEx.Equal(runStatus, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     C1, end to end: an <c>All</c> join whose one blocked branch an operator Skips carries the branch that
    ///     succeeded, and the tail behind it runs. Live, this shape ended <c>Cancelled</c> with fourteen Skipped rows
    ///     behind a join whose siblings had all done their work — the skip read as indistinguishable from a failure.
    ///     <para>
    ///         The operator's comment travels onto the row it settles. It is the only account of why the work the
    ///         downstream node expected is not there, and the objective composition quotes it verbatim.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnAllJoinCarriesOnWhenAnOperatorSkipsOneBranchAndItsSiblingSucceeded()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.AllJoinOverASkippedBranch, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "allsurvivor").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked,
            (await harness.ReadNodeRunAsync(runId, "alldoomed").ConfigureAwait(false)).Status,
            "an agent bound to nothing stands down for a human rather than settling.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "allmerge").ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "alldoomed", DevWorkflowDecisionKind.Skip, comment: "This slice names a file the repository does not have.")
                     .ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var skipped = await harness.ReadNodeRunAsync(runId, "alldoomed").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped, skipped.Status);
        AssertEx.Equal("Skipped by an operator: This slice names a file the repository does not have.",
            AssertEx.NotNull(skipped.TerminalReason),
            "the operator's own words are the whole account of why the work downstream expected is not there.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "allmerge").ConfigureAwait(false)).Status,
            "the excused branch does not take the join with it.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "alltail").ConfigureAwait(false)).Status,
            "and the tail behind the join runs, which is what the live run lost.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     FU2-3: an operator's Retry buys ONE more attempt and says why. Both halves used to be lost — the comment
    ///     reached the durable decision row and nothing else, and the attempt went past a cap that never moved, so the
    ///     row read "attempt 4 of 3" as though the runtime had broken its own budget.
    /// </summary>
    [Test]
    public async Task AnOperatorRetryWithAReason_WidensTheCapByOneAndCarriesTheReasonOntoTheNextAttempt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Three failures spend the node's declared cap, which is where a human is asked.
        for (var failure = 1; failure <= 3; failure++)
        {
            await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        var blocked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(expected: 3, blocked.Attempt);
        AssertEx.Equal(expected: 3, blocked.MaxAttempts);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry, comment: "The model kept picking the wrong file; start from src/inference.")
                     .ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(expected: 4, retried.Attempt);
        AssertEx.Equal(expected: 4, retried.MaxAttempts, "a human retry is allowed AT the cap and buys exactly one more attempt, so the cap moves with it.");
        var input = AssertEx.NotNull(retried.InputJson, "the reason has to reach the document the next attempt is composed from.");
        AssertEx.Contains(input, "operatorRetryReason");
        AssertEx.Contains(input, "start from src/inference");
        AssertEx.Contains(input, "\"operatorRetryAttempt\":4", message: "scoped to the attempt the decision started, so no later re-attempt quotes it.");
    }

    /// <summary>
    ///     A Retry with nothing typed still buys the attempt; there is simply no sentence to carry. And a SILENT retry
    ///     after a spoken one must not leave the spoken one standing: the reason belongs to the attempt it was typed
    ///     for, so the next objective would otherwise quote a person who said nothing about that try.
    /// </summary>
    [Test]
    public async Task AnOperatorRetryWithNoComment_StillWidensTheCapAndDropsThePreviousReason()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        for (var failure = 1; failure <= 3; failure++)
        {
            await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry, comment: "Start from src/inference.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).InputJson), "Start from src/inference.");

        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(expected: 5, retried.Attempt);
        AssertEx.Equal(expected: 5, retried.MaxAttempts, "a silent retry buys its attempt exactly as a spoken one does.");
        AssertEx.False((retried.InputJson ?? string.Empty).Contains("operatorRetryReason", StringComparison.Ordinal),
            "nothing was said this time, so the last person's sentence must not be handed to this attempt.");
    }

    /// <summary>
    ///     Each Retry buys ONE attempt, not an unbounded exemption: the second widening is what proves the cap tracks
    ///     the decisions rather than being switched off by the first of them.
    /// </summary>
    [Test]
    public async Task ASecondOperatorRetry_WidensTheCapAgain()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        for (var failure = 1; failure <= 3; failure++)
        {
            await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry, comment: "Once more.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // The widened cap is now spent too, so the fourth failure asks the same person again.
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var spent = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, spent.Status, "one retry buys one attempt, and the cap is what says so.");
        AssertEx.Equal(expected: 4, spent.Attempt);
        AssertEx.Equal(expected: 4, spent.MaxAttempts);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry, comment: "And again.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var again = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(expected: 5, again.Attempt);
        AssertEx.Equal(expected: 5, again.MaxAttempts);
    }
}
