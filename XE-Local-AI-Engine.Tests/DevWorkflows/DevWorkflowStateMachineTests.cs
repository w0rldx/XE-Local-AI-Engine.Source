namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run and node-run state machines. The store does not judge transitions, so these functions are the only guard
///     — and being pure is what lets the whole table be enumerated with no database in sight.
/// </summary>
public sealed class DevWorkflowStateMachineTests
{
    [Test]
    public void EdgeState_WhileTheSourceIsStillLive_IsPending()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);
        var edge = graph.OutboundEdges("research")[0];

        foreach (var status in new[]
                 {
                     DevWorkflowNodeRunStatus.Pending,
                     DevWorkflowNodeRunStatus.Queued,
                     DevWorkflowNodeRunStatus.Running,
                     DevWorkflowNodeRunStatus.WaitingForApproval,
                     DevWorkflowNodeRunStatus.Blocked
                 })
        {
            AssertEx.Equal(DevWorkflowEdgeState.Pending,
                DevWorkflowStateMachine.EdgeState(edge, graph, ByKey(NodeRun("research", status))),
                status.ToString());
        }

        AssertEx.Equal(DevWorkflowEdgeState.Pending,
            DevWorkflowStateMachine.EdgeState(edge, graph, ByKey()),
            "a source that has not been materialized yet is a wait, not a refusal.");
    }

    [Test]
    [Arguments(DevWorkflowNodeRunStatus.Failed)]
    [Arguments(DevWorkflowNodeRunStatus.Cancelled)]
    public void EdgeState_WhenTheSourceBroke_IsDead(DevWorkflowNodeRunStatus status)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        AssertEx.Equal(DevWorkflowEdgeState.Dead,
            DevWorkflowStateMachine.EdgeState(graph.OutboundEdges("research")[0], graph, ByKey(NodeRun("research", status))));
    }

    /// <summary>
    ///     A Skip is a person's decision or a cascade off one, and the graph is what tells the two apart. An ENTRY node
    ///     has nothing upstream that could have refused it, so its skip can only have been chosen — and a decision to
    ///     route around one step is not a reason to abandon the ones that worked.
    /// </summary>
    [Test]
    public void EdgeState_WhenASkipHadNothingDeadBehindIt_IsWaivedRatherThanDead()
    {
        var entry = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        AssertEx.Equal(DevWorkflowEdgeState.Waived,
            DevWorkflowStateMachine.EdgeState(entry.OutboundEdges("research")[0], entry, ByKey(NodeRun("research", DevWorkflowNodeRunStatus.Skipped))),
            "'research' has no inbound edge at all, so nothing but a person can have skipped it.");

        var gated = DevWorkflowGraph.Parse(DevWorkflowGraphs.ThreeLevelChain);

        AssertEx.Equal(DevWorkflowEdgeState.Dead,
            DevWorkflowStateMachine.EdgeState(gated.OutboundEdges("first")[0],
                gated,
                ByKey(NodeRun("gate", DevWorkflowNodeRunStatus.Succeeded, """{"passed":false}"""), NodeRun("first", DevWorkflowNodeRunStatus.Skipped))),
            "and a skip the gate above it caused stays Dead — the run was told where it may not go.");
    }

    [Test]
    public void EdgeState_OnASucceededSource_FollowsTheEdgeCondition()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var ship = graph.OutboundEdges("approve").Single(edge => edge.To == "ship");
        var revise = graph.OutboundEdges("approve").Single(edge => edge.To == "revise");
        var approved = NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, """{"decision":"Approve"}""");

        AssertEx.Equal(DevWorkflowEdgeState.Satisfied, DevWorkflowStateMachine.EdgeState(ship, graph, ByKey(approved)));
        AssertEx.Equal(DevWorkflowEdgeState.Dead, DevWorkflowStateMachine.EdgeState(revise, graph, ByKey(approved)));
    }

    /// <summary>
    ///     Unreadable output is absence, not an exception. A tick that threw here would stop advancing every other run
    ///     in the loop over one bad row.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("{ not json")]
    public void EdgeState_WhenTheSourceOutputCannotBeRead_KillsTheConditionalEdge(string? outputJson)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var ship = graph.OutboundEdges("approve").Single(edge => edge.To == "ship");

        AssertEx.Equal(DevWorkflowEdgeState.Dead,
            DevWorkflowStateMachine.EdgeState(ship, graph, ByKey(NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, outputJson))));
    }

    /// <summary>
    ///     Vacuous <c>All</c>, and it is load-bearing twice over: it is how an entry node becomes eligible at all, and it
    ///     is what lets a decomposition that produced no tasks finish instead of hanging its join forever.
    /// </summary>
    [Test]
    public void Admission_ForANodeWithNoInboundEdgesUnderAll_IsEligible()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible, DevWorkflowStateMachine.Admission(graph.Nodes["research"], graph, ByKey()));
    }

    [Test]
    public void Admission_UnderAll_WaitsForEveryBranchToSettleAndThenSkipsOnAnyDeadOne()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);
        var join = graph.Nodes["join"];

        AssertEx.Equal(DevWorkflowNodeAdmission.Wait,
            DevWorkflowStateMachine.Admission(join, graph, ByKey(NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded))),
            "one branch home, one still running.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(join,
                graph,
                ByKey(NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded), NodeRun("test", DevWorkflowNodeRunStatus.Succeeded))));

        AssertEx.Equal(DevWorkflowNodeAdmission.Wait,
            DevWorkflowStateMachine.Admission(join, graph, ByKey(NodeRun("test", DevWorkflowNodeRunStatus.Failed))),
            "dead AND pending: the join can no longer fire, but settling that in front of a branch still running skips it, "
            + "and everything after it, over work the run has not finished.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(join,
                graph,
                ByKey(NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded), NodeRun("test", DevWorkflowNodeRunStatus.Failed))),
            "an All join cannot proceed on a branch that will never arrive.");
    }

    /// <summary>
    ///     <c>Any</c> exists for the gate-merge case, and it must not fire on whichever branch happened to land first:
    ///     a sibling that could still satisfy its edge is still a reason to wait.
    /// </summary>
    [Test]
    public void Admission_UnderAny_WaitsForEveryBranchToSettleAndThenTakesOne()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var done = graph.Nodes["done"];

        AssertEx.Equal(DevWorkflowNodeAdmission.Wait,
            DevWorkflowStateMachine.Admission(done, graph, ByKey(NodeRun("ship", DevWorkflowNodeRunStatus.Succeeded))));

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(done,
                graph,
                ByKey(NodeRun("ship", DevWorkflowNodeRunStatus.Succeeded), NodeRun("revise", DevWorkflowNodeRunStatus.Skipped))),
            "the not-taken branch of a gate is dead, and the taken one carries the join.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Wait,
            DevWorkflowStateMachine.Admission(done, graph, ByKey(NodeRun("ship", DevWorkflowNodeRunStatus.Skipped))),
            "dead AND pending, the other way round: the surviving branch could still carry this join, so it is not "
            + "answered off the one that died.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(done,
                graph,
                ByKey(NodeRun("ship", DevWorkflowNodeRunStatus.Skipped), NodeRun("revise", DevWorkflowNodeRunStatus.Skipped))),
            "with every branch dead, even an Any join has nothing to carry.");
    }

    /// <summary>
    ///     The C1 ruling, and the shape the live finding took: an operator skipped one clone's implementation because
    ///     that slice could never succeed, and the <c>All</c> join skipped over its succeeding siblings, taking the
    ///     fourteen nodes behind it and the run's own status with it. A skip a person chose is excused; the join goes
    ///     on as long as a sibling actually arrived.
    /// </summary>
    [Test]
    public void Admission_UnderAll_CompletesWhenOneLeafIsSkippedAndItsSiblingSucceeded()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(graph.Nodes["join"],
                graph,
                ByKey(NodeRun("implement", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("test", DevWorkflowNodeRunStatus.Skipped))),
            "one leaf excused, one home: the join carries what arrived.");
    }

    /// <summary>
    ///     The other half of the same rule, and the reason it is read off the graph rather than off the row: a skip
    ///     BELOW something that failed is the failure travelling, not a decision, and it must kill the join exactly as
    ///     it always has.
    /// </summary>
    [Test]
    public void Admission_UnderAll_StillSkipsWhenTheSkippedLeafSitsUnderAFailure()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOutOverAFailingChain);

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(graph.Nodes["join"],
                graph,
                ByKey(NodeRun("start", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("broken", DevWorkflowNodeRunStatus.Failed),
                    NodeRun("after", DevWorkflowNodeRunStatus.Skipped))),
            "a skip with a failure behind it is the failure reaching the join, whatever the sibling did.");
    }

    /// <summary>
    ///     A gate's not-taken branch is Skipped like any other, and it is the one skip nobody chose — the condition
    ///     refused it. Excusing it would let every gate's losing branch carry a join it was never routed through.
    /// </summary>
    [Test]
    public void Admission_UnderAll_StillSkipsOnAGatesNotTakenBranch()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.GateBranchesIntoAJoin);

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(graph.Nodes["join"],
                graph,
                ByKey(NodeRun("gate", DevWorkflowNodeRunStatus.Succeeded, """{"passed":true}"""),
                    NodeRun("taken", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("nottaken", DevWorkflowNodeRunStatus.Skipped))));
    }

    /// <summary>
    ///     With every branch excused there is nothing to carry, so the join skips — and its own skip is then waived in
    ///     turn, which is how the cascade travels exactly as far as the excusing does.
    ///     <para>
    ///         The seeded <c>feature-development-v1</c> shape is the documented exception: its join also has the
    ///         decomposition's own edge, satisfied from the moment the decomposition succeeded, so a run whose every
    ///         clone was skipped still reaches verification — which then judges that nothing was produced. Accepted.
    ///     </para>
    /// </summary>
    [Test]
    public void Admission_UnderAll_SkipsWhenEveryLeafWasSkippedUnlessSomethingElseSatisfiesIt()
    {
        var fanOut = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(fanOut.Nodes["join"],
                fanOut,
                ByKey(NodeRun("implement", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("lint", DevWorkflowNodeRunStatus.Skipped),
                    NodeRun("test", DevWorkflowNodeRunStatus.Skipped))),
            "excused is not arrived: with nothing carried the join has nothing to join.");

        var seeded = DevWorkflowGraph.Parse(DevWorkflowGraphs.MaterializedDecompositionJoin);

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(seeded.Nodes["join"],
                seeded,
                ByKey(NodeRun("decompose", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("implement#one", DevWorkflowNodeRunStatus.Skipped),
                    NodeRun("implement#two", DevWorkflowNodeRunStatus.Skipped))),
            "the decomposition's own edge into the join is satisfied, and the materializer keeps it on purpose.");
    }

    /// <summary>
    ///     Where a waived skip stops travelling. The join carries the surviving branch, so the tail behind it runs —
    ///     which is the whole of what the live finding lost.
    /// </summary>
    [Test]
    public void Admission_AWaivedSkipStopsCascadingAtTheJoinThatHadASurvivor()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.AllJoinOverASkippedBranch);
        var nodeRuns = ByKey(NodeRun("allsplit", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("allsurvivor", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("alldoomed", DevWorkflowNodeRunStatus.Skipped));

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible, DevWorkflowStateMachine.Admission(graph.Nodes["allmerge"], graph, nodeRuns));

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(graph.Nodes["alltail"],
                graph,
                ByKey([.. nodeRuns.Values, NodeRun("allmerge", DevWorkflowNodeRunStatus.Succeeded)])),
            "and the tail behind it runs, which is the fourteen nodes the live run lost.");
    }

    /// <summary>
    ///     A skipped node is judged under ITS OWN join policy. An <c>Any</c> node is admitted by one satisfied edge, so
    ///     the dead sibling it never waited on says nothing about why an operator skipped it afterwards — and reading
    ///     that sibling as a refusal is what would make this skip a cascade and throw its own join's survivor away.
    /// </summary>
    [Test]
    public void EdgeState_AnAnyNodeThatWasAdmittedIsWaivedDespiteItsDeadSibling()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.AnyWorkNodeOverAMixedFanIn);
        var nodeRuns = ByKey(NodeRun("mixedsplit", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("mixedgood", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("mixedbad", DevWorkflowNodeRunStatus.Failed),
            NodeRun("mixedsibling", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("mixedwork", DevWorkflowNodeRunStatus.Skipped));

        AssertEx.Equal(DevWorkflowEdgeState.Waived,
            DevWorkflowStateMachine.EdgeState(graph.OutboundEdges("mixedwork")[0], graph, nodeRuns),
            "one satisfied edge was the whole contract it waited on, so only a person can have stopped it after that.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Eligible,
            DevWorkflowStateMachine.Admission(graph.Nodes["mixedmerge"], graph, nodeRuns),
            "and the All join behind it still carries the branch that did arrive.");
    }

    /// <summary>
    ///     The conservative half of the same rule: an <c>Any</c> node with nothing satisfied never had an admission of
    ///     its own to lose, so a dead branch beside an excused one leaves its skip a cascade, exactly as before.
    /// </summary>
    [Test]
    public void EdgeState_AnAnyNodeNothingSatisfiedStaysDead()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.AnyWorkNodeOverAMixedFanIn);
        var nodeRuns = ByKey(NodeRun("mixedsplit", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("mixedgood", DevWorkflowNodeRunStatus.Skipped),
            NodeRun("mixedbad", DevWorkflowNodeRunStatus.Failed),
            NodeRun("mixedsibling", DevWorkflowNodeRunStatus.Succeeded),
            NodeRun("mixedwork", DevWorkflowNodeRunStatus.Skipped));

        AssertEx.Equal(DevWorkflowEdgeState.Dead,
            DevWorkflowStateMachine.EdgeState(graph.OutboundEdges("mixedwork")[0], graph, nodeRuns),
            "excused beside broken and nothing arrived: the failure is still what this node is standing in front of.");

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(graph.Nodes["mixedmerge"], graph, nodeRuns));
    }

    /// <summary>
    ///     A cascaded skip records WHY, because a run whose tail was skipped is fourteen identical rows otherwise and
    ///     an operator cannot tell which one of them was the decision.
    /// </summary>
    [Test]
    public void SkipReason_NamesTheDependencyThatRefusedTheNode()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOutOverAFailingChain);

        AssertEx.Equal("Skipped: upstream 'broken' did not succeed.",
            DevWorkflowStateMachine.SkipReason(graph.Nodes["after"], graph, ByKey(NodeRun("broken", DevWorkflowNodeRunStatus.Failed))));

        AssertEx.Equal("Skipped: upstream 'after' was skipped.",
            DevWorkflowStateMachine.SkipReason(graph.Nodes["join"],
                graph,
                ByKey(NodeRun("start", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("lint", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("broken", DevWorkflowNodeRunStatus.Failed),
                    NodeRun("after", DevWorkflowNodeRunStatus.Skipped))));

        var fanOut = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal("Skipped: every step before this one was skipped.",
            DevWorkflowStateMachine.SkipReason(fanOut.Nodes["join"],
                fanOut,
                ByKey(NodeRun("implement", DevWorkflowNodeRunStatus.Succeeded),
                    NodeRun("lint", DevWorkflowNodeRunStatus.Skipped),
                    NodeRun("test", DevWorkflowNodeRunStatus.Skipped))),
            "no edge died and neither excused row carries a reason of its own — nothing to quote.");
    }

    /// <summary>
    ///     An operator's comment is free text and is cut to fit the column; the cut must not land inside a surrogate
    ///     pair, or the row keeps half an emoji as a lone surrogate.
    /// </summary>
    [Test]
    public void Bounded_NeverEndsOnTheHighHalfOfASurrogatePair()
    {
        var pairAtTheCut = new string('a', 499) + "😀";
        var bounded = DevWorkflowStateMachine.Bounded(pairAtTheCut, 500);

        AssertEx.Equal(499, bounded.Length);
        AssertEx.False(char.IsHighSurrogate(bounded[^1]));
        AssertEx.Equal("abc", DevWorkflowStateMachine.Bounded("abc", 500));
        AssertEx.Equal(new string('a', 500), DevWorkflowStateMachine.Bounded(new string('a', 500), 500));
    }

    /// <summary>
    ///     The reason a person gave has to survive the clone between them. On the seeded template an operator skips
    ///     <c>implement#task</c>, the <c>validate#task</c> behind it is skipped in turn, and the VALIDATE clone is what
    ///     a verification node's producing-ancestor walk stops at — so a validate clone that restated the skip
    ///     generically is a comment explaining the whole thing that never reached the agent asked to judge it.
    /// </summary>
    [Test]
    public void SkipReason_CarriesTheOperatorsOwnSentenceDownAChainOfExcusedSteps()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);
        var decided = NodeRun("implement", DevWorkflowNodeRunStatus.Skipped) with
        {
            TerminalReason = "Skipped by an operator: This slice names a file the repository does not have."
        };

        var validate = DevWorkflowStateMachine.SkipReason(graph.Nodes["lint"], graph, ByKey(decided));

        AssertEx.Equal("Skipped: upstream 'implement' was skipped by an operator: This slice names a file the repository does not have.", validate);

        AssertEx.Equal(
            "Skipped: upstream 'lint' was skipped: upstream 'implement' was skipped by an operator: "
            + "This slice names a file the repository does not have.",
            DevWorkflowStateMachine.SkipReason(graph.Nodes["join"],
                graph,
                ByKey(decided,
                    NodeRun("lint", DevWorkflowNodeRunStatus.Skipped) with
                    {
                        TerminalReason = validate
                    },
                    NodeRun("test", DevWorkflowNodeRunStatus.Skipped) with
                    {
                        TerminalReason = validate
                    })),
            "and one more hop still ends on the sentence the person typed.");
    }

    /// <summary>
    ///     Two dead edges, and only one of them is news: a gate taking its other branch is the graph working, so the
    ///     branch that was actually refused is named even when the routed-past one is listed first.
    /// </summary>
    [Test]
    public void SkipReason_PrefersARefusedBranchOverOneAGateMerelyRoutedPast()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.GateBranchesIntoAJoin);
        var gate = NodeRun("gate", DevWorkflowNodeRunStatus.Succeeded, """{"passed":true}""");

        AssertEx.Equal("Skipped: upstream 'nottaken' was skipped.",
            DevWorkflowStateMachine.SkipReason(graph.Nodes["join"],
                graph,
                ByKey(gate, NodeRun("taken", DevWorkflowNodeRunStatus.Succeeded), NodeRun("nottaken", DevWorkflowNodeRunStatus.Skipped))),
            "the gate's own dead edge into the join is listed first and is still not the cause.");

        AssertEx.Equal("Skipped: upstream 'gate' routed elsewhere.",
            DevWorkflowStateMachine.SkipReason(graph.Nodes["nottaken"], graph, ByKey(gate)),
            "a branch the condition did not accept did not fail — it was not taken.");
    }

    /// <summary>A skip is transitive: the skipped node's out-edges are dead, and so on all the way down.</summary>
    [Test]
    public void Admission_PropagatesASkipAcrossThreeLevels()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ThreeLevelChain);
        var nodeRuns = ByKey(NodeRun("gate", DevWorkflowNodeRunStatus.Succeeded, """{"passed":false}"""));

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip, DevWorkflowStateMachine.Admission(graph.Nodes["first"], graph, nodeRuns), "level 1");

        nodeRuns = ByKey([.. nodeRuns.Values, NodeRun("first", DevWorkflowNodeRunStatus.Skipped)]);
        AssertEx.Equal(DevWorkflowNodeAdmission.Skip, DevWorkflowStateMachine.Admission(graph.Nodes["second"], graph, nodeRuns), "level 2");

        nodeRuns = ByKey([.. nodeRuns.Values, NodeRun("second", DevWorkflowNodeRunStatus.Skipped)]);
        AssertEx.Equal(DevWorkflowNodeAdmission.Skip, DevWorkflowStateMachine.Admission(graph.Nodes["third"], graph, nodeRuns), "level 3");
    }

    [Test]
    public void Recompute_ReadsRunningWhileTheDispatcherStillHasWorkAndWaitingWhileAHumanDoes()
    {
        AssertEx.Equal(DevWorkflowRunStatus.Running,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.WaitingForApproval),
            "a queued node is the dispatcher's work, not a human's.");

        AssertEx.Equal(DevWorkflowRunStatus.Running,
            Recompute(DevWorkflowRunStatus.WaitingForApproval, DevWorkflowNodeRunStatus.Pending),
            "and the run comes back out of the wait when it has work again.");

        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.WaitingForApproval));

        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Blocked),
            "a node needing intervention is a human wait too — it is counted in the same pending-decision total.");
    }

    [Test]
    public void Recompute_TerminalizesOnlyOnceNothingIsLive()
    {
        AssertEx.Equal(DevWorkflowRunStatus.Completed, Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded));

        // RE-PINNED, ruling 1 (Slice D): over the helper's chain the LAST row is the graph's terminal node, so this is
        // a run whose end was cancelled after its first node succeeded — an abandoned tail, not a completion.
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Cancelled),
            "skipped and cancelled node runs do not block completion, but neither do they reach an end.");

        // RE-PINNED, ruling 1 (Slice D): every branch condition being false is still a real outcome, and it is not
        // success — no terminal node succeeded, so the run reads Cancelled rather than Completed.
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Skipped),
            "a run that skipped its way to the end never got there.");

        AssertEx.Equal(DevWorkflowRunStatus.Failed,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Failed));

        AssertEx.Equal(DevWorkflowRunStatus.Running,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Failed, DevWorkflowNodeRunStatus.Running),
            "a failure with a live sibling does not end the run — the sibling still has to settle.");
    }

    /// <summary>
    ///     Ruling 1 (Slice D): a run whose every node run is terminal with no terminal-node success and nothing failed
    ///     ends <c>Cancelled</c>, and says which ends it never reached. The reason is the whole point — an abandoned
    ///     tail has no failing node run to read a cause off, because nothing failed.
    /// </summary>
    [Test]
    public void Recompute_WithEveryNodeSkipped_IsCancelledAndNamesTheEndItNeverReached()
    {
        var outcome = DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running,
            Chain(3),
            [
                NodeRun("node-0", DevWorkflowNodeRunStatus.Skipped),
                NodeRun("node-1", DevWorkflowNodeRunStatus.Skipped),
                NodeRun("node-2", DevWorkflowNodeRunStatus.Skipped)
            ]);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, outcome.Status);
        AssertEx.Equal("No terminal node succeeded: 'node-2' was Skipped.", AssertEx.NotNull(outcome.TerminalReason));
        AssertEx.Null(outcome.FailureClass, "an operator Skip is a human decision, not a failure with a class.");
    }

    /// <summary>
    ///     The other shape ruling 1 names, and the one X10's drain does NOT cover: the rejection HAD an out-edge that
    ///     accepted it, took it, and the automatic gate below then matched none of its own branches — so everything
    ///     past it skipped and no end was reached. Nothing failed, so the gate's own answer is the only account of it,
    ///     which is why this one carries <c>GateRejected</c> where an operator Skip carries nothing.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowDecisionKind.Reject)]
    [Arguments(DevWorkflowDecisionKind.RequestChanges)]
    public void Recompute_WhenARefusalRoutesIntoASkippedTail_IsCancelledAsGateRejected(DevWorkflowDecisionKind decision)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.GateOnADecision);
        var outcome = DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.WaitingForApproval,
            graph,
            [
                NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, DevWorkflowStateMachine.GateOutputJson(decision), DevWorkflowNodeType.HumanGate),
                NodeRun("choose", DevWorkflowNodeRunStatus.Succeeded),
                NodeRun("ship", DevWorkflowNodeRunStatus.Skipped),
                NodeRun("revise", DevWorkflowNodeRunStatus.Skipped)
            ]);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, outcome.Status);
        AssertEx.Equal("GateRejected", AssertEx.NotNull(outcome.FailureClass));

        // The answer's own word, so a reader is never sent looking for a decision row that says something else.
        AssertEx.Equal($"No terminal node succeeded: 'ship' was Skipped, 'revise' was Skipped, after the gate 'approve' answered {decision}.",
            AssertEx.NotNull(outcome.TerminalReason));
    }

    /// <summary>
    ///     A node key is not charset-restricted, so an astral character can straddle the column's bound. Half a
    ///     surrogate pair is a broken string wherever it lands — both parities are pinned, because which side of the
    ///     cut the pair falls on depends on how long the rest of the sentence happens to be.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("x")]
    public void Recompute_WithAReasonPastTheColumnsBound_CutsBetweenRunesRatherThanInsideOne(string prefix)
    {
        var nodeKey = prefix + string.Concat(Enumerable.Repeat("\U0001F600", 400));
        var graph = DevWorkflowGraph.Parse($$"""{ "schemaVersion": 1, "nodes": [{ "nodeKey": {{JsonSerializer.Serialize(nodeKey)}}, "nodeType": "Agent" }], "edges": [] }""");

        var reason = AssertEx.NotNull(DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running, graph, [NodeRun(nodeKey, DevWorkflowNodeRunStatus.Skipped)]).TerminalReason);

        AssertEx.True(reason.Length <= 512, $"the run's terminal_reason column holds 512, and this is {reason.Length}.");
        AssertEx.False(char.IsHighSurrogate(reason[^1]), "a cut inside a surrogate pair writes an unpaired half into the column.");
    }

    /// <summary>
    ///     The C1 semantic, unchanged by ruling 1 and the reason the rule is about TERMINAL nodes rather than about
    ///     skips: the dead branch skipped, the survivor carried the <c>Any</c> join, and the join is the graph's end.
    /// </summary>
    [Test]
    public void Recompute_WhenASurvivingBranchCarriesAnAnyJoinToTheEnd_IsCompleted()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.AnyJoinOverADeadBranch);
        var outcome = DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running,
            graph,
            [
                NodeRun("anysplit", DevWorkflowNodeRunStatus.Succeeded),
                NodeRun("anysurvivor", DevWorkflowNodeRunStatus.Succeeded),
                NodeRun("anydoomed", DevWorkflowNodeRunStatus.Skipped),
                NodeRun("anymerge", DevWorkflowNodeRunStatus.Succeeded)
            ]);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, outcome.Status);
        AssertEx.Null(outcome.TerminalReason, "a run that reached its end has nothing to explain.");
    }

    /// <summary>
    ///     A skipped branch is not an end that failed to arrive, so long as SOME end did: the approve branch shipped,
    ///     the revise branch was skipped, and the join both feed reads Completed exactly as it did before ruling 1.
    /// </summary>
    [Test]
    public void Recompute_WithATerminalNodeSucceededBesideSkippedSiblings_IsCompleted()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var outcome = DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running,
            graph,
            [
                NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, DevWorkflowStateMachine.GateOutputJson(DevWorkflowDecisionKind.Approve), DevWorkflowNodeType.HumanGate),
                NodeRun("ship", DevWorkflowNodeRunStatus.Succeeded),
                NodeRun("revise", DevWorkflowNodeRunStatus.Skipped),
                NodeRun("done", DevWorkflowNodeRunStatus.Succeeded)
            ]);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, outcome.Status);
    }

    /// <summary>
    ///     <c>Failed</c> precedence is unchanged by ruling 1, and deliberately outranks the new answer: a run with a
    ///     failed node has a cause worth reporting, and calling that "cancelled" would bury it.
    /// </summary>
    [Test]
    public void Recompute_WithAFailedNodeAndNoTerminalSuccess_IsStillFailed()
    {
        var outcome = DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running,
            Chain(2),
            [NodeRun("node-0", DevWorkflowNodeRunStatus.Failed), NodeRun("node-1", DevWorkflowNodeRunStatus.Skipped)]);

        AssertEx.Equal(DevWorkflowRunStatus.Failed, outcome.Status);
        AssertEx.Null(outcome.FailureClass, "the failing node run already carries the class that explains it.");
    }

    [Test]
    public void Recompute_LeavesADrainingOrSettledRunAlone()
    {
        foreach (var status in new[]
                 {
                     DevWorkflowRunStatus.Pausing,
                     DevWorkflowRunStatus.Cancelling,
                     DevWorkflowRunStatus.Paused,
                     DevWorkflowRunStatus.Completed,
                     DevWorkflowRunStatus.Failed,
                     DevWorkflowRunStatus.Cancelled
                 })
        {
            AssertEx.Equal(status, Recompute(status, DevWorkflowNodeRunStatus.Succeeded), status.ToString());
        }

        AssertEx.Equal(DevWorkflowRunStatus.Pending,
            DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Pending, Chain(1), []).Status,
            "a run with no node runs has not been materialized yet; it is not complete.");
    }

    [Test]
    [Arguments(DevWorkflowRunStatus.Pending, DevWorkflowWorkItemStatus.Active)]
    [Arguments(DevWorkflowRunStatus.Running, DevWorkflowWorkItemStatus.Active)]
    [Arguments(DevWorkflowRunStatus.Pausing, DevWorkflowWorkItemStatus.Active)]
    [Arguments(DevWorkflowRunStatus.Paused, DevWorkflowWorkItemStatus.Active)]
    [Arguments(DevWorkflowRunStatus.WaitingForApproval, DevWorkflowWorkItemStatus.Blocked)]
    [Arguments(DevWorkflowRunStatus.Completed, DevWorkflowWorkItemStatus.Completed)]
    [Arguments(DevWorkflowRunStatus.Cancelled, DevWorkflowWorkItemStatus.Cancelled)]
    [Arguments(DevWorkflowRunStatus.Failed, DevWorkflowWorkItemStatus.Blocked)]
    public void WorkItemStatusFor_MapsAFailedRunToBlockedRatherThanDone(DevWorkflowRunStatus runStatus, DevWorkflowWorkItemStatus expected) =>
        AssertEx.Equal(expected, DevWorkflowStateMachine.WorkItemStatusFor(runStatus, [NodeRun("node", DevWorkflowNodeRunStatus.Running)]));

    /// <summary>
    ///     A blocked node run blocks the work item even while the run reads Running because a sibling is still working.
    ///     Reading only the run status would leave the item Active with a node nobody is coming to unblock, which is
    ///     precisely what the work-item list exists to surface.
    /// </summary>
    [Test]
    public void WorkItemStatusFor_WithABlockedNodeRunUnderARunningRun_IsBlocked()
    {
        IReadOnlyList<DevWorkflowNodeRunSnapshot> mixed =
        [
            NodeRun("busy", DevWorkflowNodeRunStatus.Running),
            NodeRun("stuck", DevWorkflowNodeRunStatus.Blocked)
        ];

        AssertEx.Equal(DevWorkflowRunStatus.Running,
            DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Running, Chain(2), mixed).Status,
            "the sibling is still the dispatcher's work.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, DevWorkflowStateMachine.WorkItemStatusFor(DevWorkflowRunStatus.Running, mixed));

        // And a terminal run still maps from its own status: a completed run is done whatever its rows once said.
        AssertEx.Equal(DevWorkflowWorkItemStatus.Completed, DevWorkflowStateMachine.WorkItemStatusFor(DevWorkflowRunStatus.Completed, mixed));
    }

    /// <summary>
    ///     Which statuses may write <c>Cancelled</c> at all. A terminal written over LIVE node runs strands them under
    ///     a run nothing advances again, and their executors' slots leak for the process lifetime — so a run with work
    ///     still in flight reaches the terminal only through the drain.
    ///     <para>
    ///         RE-PINNED, ruling 1 (Slice D): <c>Running</c> and <c>WaitingForApproval</c> gained the direct edge,
    ///         because the recomputation that writes it is reachable only once every node run is already terminal.
    ///         There is nothing left to strand there, and draining would cost a whole tick to settle what is known.
    ///     </para>
    /// </summary>
    [Test]
    public void IsLegal_ForARun_ReachesCancelledOnlyFromAQuiescentRunOrTheDrain()
    {
        DevWorkflowRunStatus[] allowed =
        [
            DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Running,
            DevWorkflowRunStatus.WaitingForApproval
        ];

        foreach (var from in Enum.GetValues<DevWorkflowRunStatus>().Where(status => !allowed.Contains(status)))
        {
            AssertEx.False(DevWorkflowStateMachine.IsLegal(from, DevWorkflowRunStatus.Cancelled), $"{from} → Cancelled must go through Cancelling.");
        }

        foreach (var from in allowed)
        {
            AssertEx.True(DevWorkflowStateMachine.IsLegal(from, DevWorkflowRunStatus.Cancelled), $"{from} → Cancelled");
        }
    }

    [Test]
    public void IsLegal_ForARun_AcceptsTheDesignedEdgesAndRefusesTheRest()
    {
        foreach (var (from, to) in new[]
                 {
                     (DevWorkflowRunStatus.Pending, DevWorkflowRunStatus.Running),
                     (DevWorkflowRunStatus.Pending, DevWorkflowRunStatus.Failed),
                     (DevWorkflowRunStatus.Running, DevWorkflowRunStatus.WaitingForApproval),
                     (DevWorkflowRunStatus.Running, DevWorkflowRunStatus.Pausing),
                     (DevWorkflowRunStatus.Running, DevWorkflowRunStatus.Completed),
                     (DevWorkflowRunStatus.WaitingForApproval, DevWorkflowRunStatus.Running),
                     (DevWorkflowRunStatus.WaitingForApproval, DevWorkflowRunStatus.Completed),
                     (DevWorkflowRunStatus.Pausing, DevWorkflowRunStatus.Paused),
                     (DevWorkflowRunStatus.Paused, DevWorkflowRunStatus.Running)
                 })
        {
            AssertEx.True(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }

        foreach (var (from, to) in new[]
                 {
                     (DevWorkflowRunStatus.Running, DevWorkflowRunStatus.Paused),
                     (DevWorkflowRunStatus.Pending, DevWorkflowRunStatus.Completed),
                     (DevWorkflowRunStatus.Paused, DevWorkflowRunStatus.Completed),
                     (DevWorkflowRunStatus.Completed, DevWorkflowRunStatus.Running),
                     (DevWorkflowRunStatus.Failed, DevWorkflowRunStatus.Running),
                     (DevWorkflowRunStatus.Cancelled, DevWorkflowRunStatus.Running)
                 })
        {
            AssertEx.False(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }
    }

    [Test]
    public void IsLegal_ForANodeRun_AcceptsTheDesignedEdgesAndRefusesTheRest()
    {
        foreach (var (from, to) in new[]
                 {
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Queued),

                     // The inline lane, which waits for no slot and so must not write a queue reason it has no token for.
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Running),
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Skipped),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Running),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Running, DevWorkflowNodeRunStatus.WaitingForApproval),
                     (DevWorkflowNodeRunStatus.Running, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Running, DevWorkflowNodeRunStatus.Blocked),
                     (DevWorkflowNodeRunStatus.WaitingForApproval, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Blocked, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Blocked, DevWorkflowNodeRunStatus.Skipped),
                     (DevWorkflowNodeRunStatus.Blocked, DevWorkflowNodeRunStatus.Failed),

                     // The fix loop's reset (X9), and the only way out of a terminal status: a node run downstream of a
                     // node being re-attempted holds an answer about work that is being replaced.
                     (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Failed, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Cancelled, DevWorkflowNodeRunStatus.Pending),

                     // An open gate is reset too: it is being asked to approve work that is being replaced, so it is
                     // re-asked rather than answered about the old round. That is the opposite of X3's walk-past.
                     (DevWorkflowNodeRunStatus.WaitingForApproval, DevWorkflowNodeRunStatus.Pending)
                 })
        {
            AssertEx.True(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }

        foreach (var (from, to) in new[]
                 {
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.WaitingForApproval),

                     // A reset is the ONLY move out of a terminal status: it goes back to the start of an attempt, never
                     // sideways into one that is already under way.
                     (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Running),
                     (DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Queued),
                     (DevWorkflowNodeRunStatus.Cancelled, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Failed, DevWorkflowNodeRunStatus.Blocked),

                     // X3: Skip is an intervention on a Blocked row, never a way past an open gate.
                     (DevWorkflowNodeRunStatus.WaitingForApproval, DevWorkflowNodeRunStatus.Skipped)
                 })
        {
            AssertEx.False(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }
    }

    [Test]
    public void EnsureLegal_RejectsAnIllegalMoveThroughTheStoresRejectionChannel()
    {
        AssertEx.Contains(AssertEx.Throws<DevWorkflowInvalidTransitionException>(() =>
                DevWorkflowStateMachine.EnsureLegal(DevWorkflowRunStatus.Completed, DevWorkflowRunStatus.Running)).Message,
            "cannot move to Running");

        AssertEx.Contains(AssertEx.Throws<DevWorkflowInvalidTransitionException>(() =>
                DevWorkflowStateMachine.EnsureLegal(DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Succeeded, "plan")).Message,
            "Node run 'plan' is Pending");
    }

    /// <summary>
    ///     One recomputation over a chain <c>node-0 -> node-1 -> ...</c> as long as the statuses named, so the LAST of
    ///     them is the graph's one terminal node. Ruling 1 (Slice D) reads completion off a terminal node, so a graph
    ///     is no longer optional here: a helper that invented anonymous rows with no edges cannot ask the question.
    /// </summary>
    private static DevWorkflowRunStatus Recompute(DevWorkflowRunStatus current, params DevWorkflowNodeRunStatus[] nodeRuns) =>
        DevWorkflowStateMachine.Recompute(current, Chain(nodeRuns.Length), [.. nodeRuns.Select((status, index) => NodeRun($"node-{index}", status))]).Status;

    private static DevWorkflowGraph Chain(int length)
    {
        var nodes = string.Join(", ", Enumerable.Range(0, length).Select(static index => $$"""{ "nodeKey": "node-{{index}}", "nodeType": "Agent" }"""));
        var edges = string.Join(", ", Enumerable.Range(1, Math.Max(length - 1, 0)).Select(static index => $$"""{ "from": "node-{{index - 1}}", "to": "node-{{index}}" }"""));
        return DevWorkflowGraph.Parse($$"""{ "schemaVersion": 1, "nodes": [{{nodes}}], "edges": [{{edges}}] }""");
    }

    private static Dictionary<string, DevWorkflowNodeRunSnapshot> ByKey(params DevWorkflowNodeRunSnapshot[] nodeRuns) =>
        nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);

    private static DevWorkflowNodeRunSnapshot NodeRun(string nodeKey,
        DevWorkflowNodeRunStatus status,
        string? outputJson = null,
        DevWorkflowNodeType nodeType = DevWorkflowNodeType.Agent) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            nodeKey,
            nodeType,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            status,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 1,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            outputJson,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: null,
            EndedAtUtc: null,
            CreatedAtUtc: 0);
}
