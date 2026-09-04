namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run and node-run state machines. The store does not judge transitions, so these functions are the only guard
///     — and being pure is what lets the whole table be enumerated with no database in sight.
/// </summary>
public sealed class GraphWorkflowStateMachineTests
{
    /// <summary>The output document of a Condition node whose answer routes down the false branch.</summary>
    private const string NotOk = """{"output":{"json":{"ok":false}}}""";

    [Test]
    public void EdgeState_WhileTheSourceIsStillLive_IsPending()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);
        var edge = graph.OutboundEdges("analyze")[0];

        foreach (var status in new[]
                 {
                     GraphWorkflowNodeRunStatus.Pending,
                     GraphWorkflowNodeRunStatus.Queued,
                     GraphWorkflowNodeRunStatus.Running,
                     GraphWorkflowNodeRunStatus.WaitingForApproval
                 })
        {
            AssertEx.Equal(GraphWorkflowEdgeState.Pending, GraphWorkflowStateMachine.EdgeState(edge, ByKey(NodeRun("analyze", status))), status.ToString());
        }

        AssertEx.Equal(GraphWorkflowEdgeState.Pending,
            GraphWorkflowStateMachine.EdgeState(edge, ByKey()),
            "a source that has not been materialized yet is a wait, not a refusal.");
    }

    /// <summary>
    ///     Every terminal status but <c>Succeeded</c> kills the out-edge — a skip included, because v1 has no decision
    ///     kind that can excuse one.
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowNodeRunStatus.Failed)]
    [Arguments(GraphWorkflowNodeRunStatus.Cancelled)]
    [Arguments(GraphWorkflowNodeRunStatus.Skipped)]
    public void EdgeState_WhenTheSourceBroke_IsDead(GraphWorkflowNodeRunStatus status)
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        AssertEx.Equal(GraphWorkflowEdgeState.Dead, GraphWorkflowStateMachine.EdgeState(graph.OutboundEdges("analyze")[0], ByKey(NodeRun("analyze", status))));
    }

    [Test]
    public void EdgeState_OnASucceededSource_FollowsTheEdgeCondition()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var review = graph.OutboundEdges("check").Single(static edge => edge.To == "review");
        var ship = graph.OutboundEdges("check").Single(static edge => edge.To == "ship");
        var judged = NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, """{"output":{"json":{"requiresReview":true}}}""");

        AssertEx.Equal(GraphWorkflowEdgeState.Satisfied, GraphWorkflowStateMachine.EdgeState(review, ByKey(judged)));
        AssertEx.Equal(GraphWorkflowEdgeState.Dead, GraphWorkflowStateMachine.EdgeState(ship, ByKey(judged)));
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
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var review = graph.OutboundEdges("check").Single(static edge => edge.To == "review");

        AssertEx.Equal(GraphWorkflowEdgeState.Dead,
            GraphWorkflowStateMachine.EdgeState(review, ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, outputJson))));
    }

    /// <summary>
    ///     An <c>Any</c> node whose every inbound edge is dead stays dead: nothing arrived, so it never had an admission
    ///     of its own, and the merge that exists to carry ONE live branch has no branch to carry.
    /// </summary>
    [Test]
    public void EdgeState_AnAnyNodeNothingSatisfiedStaysDead()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAny);
        var nodeRuns = ByKey(NodeRun("start", GraphWorkflowNodeRunStatus.Succeeded),
            NodeRun("fanout", GraphWorkflowNodeRunStatus.Succeeded),
            NodeRun("left", GraphWorkflowNodeRunStatus.Skipped),
            NodeRun("right", GraphWorkflowNodeRunStatus.Failed),
            NodeRun("merge", GraphWorkflowNodeRunStatus.Skipped));

        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip, GraphWorkflowStateMachine.Admission(graph.Nodes["merge"], graph, nodeRuns));
        AssertEx.Equal(GraphWorkflowEdgeState.Dead, GraphWorkflowStateMachine.EdgeState(graph.OutboundEdges("merge")[0], nodeRuns));
    }

    /// <summary>
    ///     Vacuous <c>All</c>, and it is load-bearing rather than pedantic: it is how the Start node becomes eligible at
    ///     all.
    /// </summary>
    [Test]
    public void Admission_ForANodeWithNoInboundEdgesUnderAll_IsEligible()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        AssertEx.Equal(GraphWorkflowNodeAdmission.Eligible, GraphWorkflowStateMachine.Admission(graph.Nodes["start"], graph, ByKey()));
    }

    /// <summary>
    ///     Asked of <c>summary</c>, which is an ORDINARY Agent node with two inbound edges rather than a <c>Join</c>.
    ///     The join policy is a property of every node, and reading it off <c>Join</c> alone is the documented trap.
    /// </summary>
    [Test]
    public void Admission_UnderAll_WaitsForEveryBranchToSettleAndThenSkipsOnAnyDeadOne()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAll);
        var summary = graph.Nodes["summary"];

        AssertEx.Equal(GraphWorkflowNodeAdmission.Wait,
            GraphWorkflowStateMachine.Admission(summary, graph, ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded))),
            "one branch home, one still running.");

        AssertEx.Equal(GraphWorkflowNodeAdmission.Eligible,
            GraphWorkflowStateMachine.Admission(summary,
                graph,
                ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded), NodeRun("right", GraphWorkflowNodeRunStatus.Succeeded))));

        AssertEx.Equal(GraphWorkflowNodeAdmission.Wait,
            GraphWorkflowStateMachine.Admission(summary, graph, ByKey(NodeRun("right", GraphWorkflowNodeRunStatus.Failed))),
            "dead AND pending: it can no longer fire, but settling that in front of a branch still running skips it, "
            + "and everything after it, over work the run has not finished.");

        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip,
            GraphWorkflowStateMachine.Admission(summary,
                graph,
                ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded), NodeRun("right", GraphWorkflowNodeRunStatus.Failed))),
            "an All join cannot proceed on a branch that will never arrive.");
    }

    /// <summary>
    ///     <c>Any</c> must not fire on whichever branch happened to land first: a sibling that could still satisfy its
    ///     edge is still a reason to wait.
    /// </summary>
    [Test]
    public void Admission_UnderAny_WaitsForEveryBranchToSettleAndThenTakesOne()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAny);
        var merge = graph.Nodes["merge"];

        AssertEx.Equal(GraphWorkflowNodeAdmission.Wait,
            GraphWorkflowStateMachine.Admission(merge, graph, ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded))));

        AssertEx.Equal(GraphWorkflowNodeAdmission.Eligible,
            GraphWorkflowStateMachine.Admission(merge,
                graph,
                ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded), NodeRun("right", GraphWorkflowNodeRunStatus.Skipped))),
            "one branch died, the other carries the merge.");

        AssertEx.Equal(GraphWorkflowNodeAdmission.Wait,
            GraphWorkflowStateMachine.Admission(merge, graph, ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Skipped))),
            "dead AND pending, the other way round: the surviving branch could still carry this merge.");

        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip,
            GraphWorkflowStateMachine.Admission(merge,
                graph,
                ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Skipped), NodeRun("right", GraphWorkflowNodeRunStatus.Skipped))),
            "with every branch dead, even an Any merge has nothing to carry.");

        // 'done' is an END node carrying joinPolicy Any. The policy is a property of EVERY node, so a runtime that
        // read it off Join nodes alone would fall back to All here and skip a node one live branch reached.
        var branching = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);

        AssertEx.Equal(GraphWorkflowNodeAdmission.Eligible,
            GraphWorkflowStateMachine.Admission(branching.Nodes["done"],
                branching,
                ByKey(NodeRun("review", GraphWorkflowNodeRunStatus.Succeeded), NodeRun("ship", GraphWorkflowNodeRunStatus.Skipped))),
            "an ordinary node under Any takes the branch that arrived.");
    }

    /// <summary>
    ///     A Condition node's not-taken branch is Skipped like any other, and it must kill an <c>All</c> join it feeds:
    ///     nothing chose it, the graph refused it.
    /// </summary>
    [Test]
    public void Admission_UnderAll_StillSkipsOnAGatesNotTakenBranch()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ConditionWithDefault);

        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip,
            GraphWorkflowStateMachine.Admission(graph.Nodes["merge"],
                graph,
                ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, NotOk),
                    NodeRun("yes", GraphWorkflowNodeRunStatus.Skipped),
                    NodeRun("fallback", GraphWorkflowNodeRunStatus.Succeeded))));
    }

    /// <summary>A skip is transitive: the skipped node's out-edges are dead, and so on all the way down.</summary>
    [Test]
    public void Admission_PropagatesASkipAcrossThreeLevels()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ConditionWithDefault);
        var nodeRuns = ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, NotOk), NodeRun("fallback", GraphWorkflowNodeRunStatus.Succeeded));

        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip, GraphWorkflowStateMachine.Admission(graph.Nodes["yes"], graph, nodeRuns), "level 1");

        nodeRuns = ByKey([.. nodeRuns.Values, NodeRun("yes", GraphWorkflowNodeRunStatus.Skipped)]);
        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip, GraphWorkflowStateMachine.Admission(graph.Nodes["merge"], graph, nodeRuns), "level 2");

        nodeRuns = ByKey([.. nodeRuns.Values, NodeRun("merge", GraphWorkflowNodeRunStatus.Skipped)]);
        AssertEx.Equal(GraphWorkflowNodeAdmission.Skip, GraphWorkflowStateMachine.Admission(graph.Nodes["done"], graph, nodeRuns), "level 3");
    }

    /// <summary>
    ///     A cascaded skip records WHY, because a run whose tail was skipped is a column of identical rows otherwise and
    ///     an operator cannot tell which one of them was the cause.
    /// </summary>
    [Test]
    public void SkipReason_NamesTheDependencyThatRefusedTheNode()
    {
        var condition = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ConditionWithDefault);

        AssertEx.Equal("Skipped: upstream 'check' routed elsewhere.",
            GraphWorkflowStateMachine.SkipReason(condition.Nodes["yes"], condition, ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, NotOk))));

        AssertEx.Equal("Skipped: upstream 'yes' was skipped.",
            GraphWorkflowStateMachine.SkipReason(condition.Nodes["merge"],
                condition,
                ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, NotOk),
                    NodeRun("yes", GraphWorkflowNodeRunStatus.Skipped),
                    NodeRun("fallback", GraphWorkflowNodeRunStatus.Succeeded))));

        var parallel = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAll);

        AssertEx.Equal("Skipped: upstream 'left' did not succeed.",
            GraphWorkflowStateMachine.SkipReason(parallel.Nodes["merge"],
                parallel,
                ByKey(NodeRun("left", GraphWorkflowNodeRunStatus.Failed), NodeRun("right", GraphWorkflowNodeRunStatus.Succeeded))));
    }

    /// <summary>
    ///     Two dead edges, and only one of them is news: a Condition taking its other branch is the graph working, so
    ///     the branch that was actually refused is named even when the routed-past one is listed first.
    /// </summary>
    [Test]
    public void SkipReason_PrefersARefusedBranchOverOneAGateMerelyRoutedPast()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ConditionWithDefault);

        AssertEx.Equal("Skipped: upstream 'yes' was skipped.",
            GraphWorkflowStateMachine.SkipReason(graph.Nodes["merge"],
                graph,
                ByKey(NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, NotOk),
                    NodeRun("yes", GraphWorkflowNodeRunStatus.Skipped),
                    NodeRun("fallback", GraphWorkflowNodeRunStatus.Succeeded))),
            "the Condition's own dead edge into the join is listed first and is still not the cause.");
    }

    /// <summary>
    ///     A reason is cut to fit its column, and the cut must not land inside a surrogate pair, or the row keeps half
    ///     an emoji as a lone surrogate.
    /// </summary>
    [Test]
    public void Bounded_NeverEndsOnTheHighHalfOfASurrogatePair()
    {
        var pairAtTheCut = new string('a', 499) + "😀";
        var bounded = GraphWorkflowStateMachine.Bounded(pairAtTheCut, 500);

        AssertEx.Equal(expected: 499, bounded.Length);
        AssertEx.False(char.IsHighSurrogate(bounded[^1]));
        AssertEx.Equal("abc", GraphWorkflowStateMachine.Bounded("abc", 500));
        AssertEx.Equal(new string('a', 500), GraphWorkflowStateMachine.Bounded(new string('a', 500), 500));
    }

    [Test]
    public void Recompute_ReadsRunningWhileTheDispatcherStillHasWorkAndWaitingWhileAHumanDoes()
    {
        AssertEx.Equal(GraphWorkflowRunStatus.Running,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Queued, GraphWorkflowNodeRunStatus.WaitingForApproval),
            "a queued node is the dispatcher's work, not a human's.");

        AssertEx.Equal(GraphWorkflowRunStatus.Running,
            Recompute(GraphWorkflowRunStatus.WaitingForApproval, GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Pending),
            "and the run comes back out of the wait when it has work again.");

        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Succeeded, GraphWorkflowNodeRunStatus.WaitingForApproval));
    }

    [Test]
    public void Recompute_TerminalizesOnlyOnceNothingIsLive()
    {
        AssertEx.Equal(GraphWorkflowRunStatus.Completed,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Succeeded, GraphWorkflowNodeRunStatus.Succeeded));

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled,
            Recompute(GraphWorkflowRunStatus.Running,
                GraphWorkflowNodeRunStatus.Succeeded,
                GraphWorkflowNodeRunStatus.Skipped,
                GraphWorkflowNodeRunStatus.Cancelled),
            "skipped and cancelled node runs do not block completion, but neither do they reach an end.");

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Skipped, GraphWorkflowNodeRunStatus.Skipped),
            "a run that skipped its way to the end never got there.");

        AssertEx.Equal(GraphWorkflowRunStatus.Failed,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Succeeded, GraphWorkflowNodeRunStatus.Failed));

        AssertEx.Equal(GraphWorkflowRunStatus.Running,
            Recompute(GraphWorkflowRunStatus.Running, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowNodeRunStatus.Running),
            "a failure with a live sibling does not end the run — the sibling still has to settle.");
    }

    /// <summary>
    ///     A run whose every node run is terminal with no End succeeded and nothing failed ends <c>Cancelled</c>, and
    ///     says which ends it never reached. The reason is the whole point: an abandoned tail has no failing node run to
    ///     read a cause off, because nothing failed.
    /// </summary>
    [Test]
    public void Recompute_WithEveryNodeSkipped_IsCancelledAndNamesTheEndItNeverReached()
    {
        var outcome = GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.Running,
            Chain(3),
            [
                NodeRun("node-0", GraphWorkflowNodeRunStatus.Skipped),
                NodeRun("node-1", GraphWorkflowNodeRunStatus.Skipped),
                NodeRun("node-2", GraphWorkflowNodeRunStatus.Skipped)
            ]);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, outcome.Status);
        AssertEx.Equal("No terminal node succeeded: 'node-2' was Skipped.", AssertEx.NotNull(outcome.TerminalReason));
        AssertEx.Equal(GraphWorkflowFailureClass.None, outcome.FailureClass, "nothing refused this run; it simply never arrived.");
    }

    /// <summary>
    ///     The rejection HAD an out-edge that accepted it — the pre-flight rule guarantees one — took it, and the
    ///     Condition below matched none of its own branches, so everything past it skipped and no end was reached.
    ///     Nothing failed, so the pause's own answer is the only account of it.
    /// </summary>
    [Test]
    public void Recompute_WhenAPauseIsRejectedAndNothingRoutes_IsCancelledAsGateRejected()
    {
        const string RejectionIntoADeadEnd = """
                                             { "schemaVersion": 1,
                                               "nodes": [{ "key": "start", "kind": "Start" },
                                                         { "key": "review", "kind": "Pause",
                                                           "config": { "prompt": "Well?", "allowedDecisions": ["Approve", "Reject"] } },
                                                         { "key": "shipped", "kind": "End", "config": { "outcome": "x" } },
                                                         { "key": "check", "kind": "Condition", "config": { "path": "output.status" } },
                                                         { "key": "a", "kind": "End", "config": { "outcome": "y" } },
                                                         { "key": "b", "kind": "End", "config": { "outcome": "z" } }],
                                               "edges": [{ "key": "e1", "from": "start", "to": "review" },
                                                         { "key": "e2", "from": "review", "to": "shipped",
                                                           "condition": { "path": "output.decision", "op": "eq", "value": "Approve" } },
                                                         { "key": "e3", "from": "review", "to": "check",
                                                           "condition": { "path": "output.decision", "op": "eq", "value": "Reject" } },
                                                         { "key": "e4", "from": "check", "to": "a", "condition": { "op": "eq", "value": "never" } },
                                                         { "key": "e5", "from": "check", "to": "b", "condition": { "op": "eq", "value": "alsonever" } }] }
                                             """;

        var outcome = GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.WaitingForApproval,
            GraphWorkflowGraph.Parse(RejectionIntoADeadEnd),
            [
                NodeRun("review",
                    GraphWorkflowNodeRunStatus.Succeeded,
                    GraphWorkflowStateMachine.PauseOutputJson(GraphWorkflowDecisionKind.Reject),
                    GraphWorkflowNodeKind.Pause),
                NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, """{"output":{"status":"neither"}}""", GraphWorkflowNodeKind.Condition),
                NodeRun("shipped", GraphWorkflowNodeRunStatus.Skipped),
                NodeRun("a", GraphWorkflowNodeRunStatus.Skipped),
                NodeRun("b", GraphWorkflowNodeRunStatus.Skipped)
            ]);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, outcome.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.GateRejected, outcome.FailureClass);

        // The answer's own word, so a reader is never sent looking for a decision row that says something else.
        AssertEx.Equal("No terminal node succeeded: 'shipped' was Skipped, 'a' was Skipped, 'b' was Skipped, after the pause 'review' answered Reject.",
            AssertEx.NotNull(outcome.TerminalReason));
    }

    /// <summary>
    ///     The dead branch skipped, the survivor carried the <c>Any</c> merge, and the run reached its End. That the
    ///     rule is about TERMINAL nodes rather than about skips is what makes this a completion.
    /// </summary>
    [Test]
    public void Recompute_WhenASurvivingBranchCarriesAnAnyJoinToTheEnd_IsCompleted()
    {
        var outcome = GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.Running,
            GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAny),
            [
                NodeRun("start", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("fanout", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("left", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("right", GraphWorkflowNodeRunStatus.Skipped),
                NodeRun("merge", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("done", GraphWorkflowNodeRunStatus.Succeeded)
            ]);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, outcome.Status);
        AssertEx.Null(outcome.TerminalReason, "a run that reached its end has nothing to explain.");
    }

    /// <summary>A skipped branch is not an end that failed to arrive, so long as SOME end did.</summary>
    [Test]
    public void Recompute_WithATerminalNodeSucceededBesideSkippedSiblings_IsCompleted()
    {
        var outcome = GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.Running,
            GraphWorkflowGraph.Parse(GraphWorkflowGraphs.TwoEnds),
            [
                NodeRun("start", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("check", GraphWorkflowNodeRunStatus.Succeeded, """{"output":{"json":{"ok":true}}}""", GraphWorkflowNodeKind.Condition),
                NodeRun("okend", GraphWorkflowNodeRunStatus.Succeeded),
                NodeRun("badend", GraphWorkflowNodeRunStatus.Skipped)
            ]);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, outcome.Status);
    }

    /// <summary>
    ///     <c>Failed</c> outranks the cancelled answer deliberately: a run with a failed node has a cause worth
    ///     reporting, and calling that "cancelled" would bury it.
    /// </summary>
    [Test]
    public void Recompute_WithAFailedNodeAndNoTerminalSuccess_IsStillFailed()
    {
        var outcome = GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.Running,
            Chain(2),
            [NodeRun("node-0", GraphWorkflowNodeRunStatus.Failed), NodeRun("node-1", GraphWorkflowNodeRunStatus.Skipped)]);

        AssertEx.Equal(GraphWorkflowRunStatus.Failed, outcome.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.None, outcome.FailureClass, "the failing node run already carries the class that explains it.");
    }

    [Test]
    public void Recompute_LeavesADrainingOrSettledRunAlone()
    {
        foreach (var status in new[]
                 {
                     GraphWorkflowRunStatus.Cancelling,
                     GraphWorkflowRunStatus.Completed,
                     GraphWorkflowRunStatus.Failed,
                     GraphWorkflowRunStatus.Cancelled
                 })
        {
            AssertEx.Equal(status, Recompute(status, GraphWorkflowNodeRunStatus.Succeeded, GraphWorkflowNodeRunStatus.Succeeded), status.ToString());
        }

        AssertEx.Equal(GraphWorkflowRunStatus.Pending,
            GraphWorkflowStateMachine.Recompute(GraphWorkflowRunStatus.Pending, Chain(2), []).Status,
            "a run with no node runs has not been materialized yet; it is not complete.");
    }

    /// <summary>
    ///     Which statuses may write <c>Cancelled</c> at all. A terminal written over LIVE node runs strands them under a
    ///     run nothing advances again, so a run with work still in flight reaches the terminal only through the drain.
    ///     <c>Running</c> and <c>WaitingForApproval</c> have the direct edge because the recomputation that writes it is
    ///     reachable only once every node run is already terminal.
    /// </summary>
    [Test]
    public void IsLegal_ForARun_ReachesCancelledOnlyFromAQuiescentRunOrTheDrain()
    {
        GraphWorkflowRunStatus[] allowed =
        [
            GraphWorkflowRunStatus.Cancelling,
            GraphWorkflowRunStatus.Running,
            GraphWorkflowRunStatus.WaitingForApproval
        ];

        foreach (var from in Enum.GetValues<GraphWorkflowRunStatus>().Where(status => !allowed.Contains(status)))
        {
            AssertEx.False(GraphWorkflowStateMachine.IsLegal(from, GraphWorkflowRunStatus.Cancelled), $"{from} → Cancelled must go through Cancelling.");
        }

        foreach (var from in allowed)
        {
            AssertEx.True(GraphWorkflowStateMachine.IsLegal(from, GraphWorkflowRunStatus.Cancelled), $"{from} → Cancelled");
        }
    }

    [Test]
    public void IsLegal_ForARun_AcceptsTheDesignedEdgesAndRefusesTheRest()
    {
        foreach (var (from, to) in new[]
                 {
                     (GraphWorkflowRunStatus.Pending, GraphWorkflowRunStatus.Running),
                     (GraphWorkflowRunStatus.Pending, GraphWorkflowRunStatus.Failed),
                     (GraphWorkflowRunStatus.Running, GraphWorkflowRunStatus.WaitingForApproval),
                     (GraphWorkflowRunStatus.Running, GraphWorkflowRunStatus.Cancelling),
                     (GraphWorkflowRunStatus.Running, GraphWorkflowRunStatus.Completed),
                     (GraphWorkflowRunStatus.WaitingForApproval, GraphWorkflowRunStatus.Running),
                     (GraphWorkflowRunStatus.WaitingForApproval, GraphWorkflowRunStatus.Completed),
                     (GraphWorkflowRunStatus.Cancelling, GraphWorkflowRunStatus.Cancelled)
                 })
        {
            AssertEx.True(GraphWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }

        foreach (var (from, to) in new[]
                 {
                     (GraphWorkflowRunStatus.Pending, GraphWorkflowRunStatus.Completed),
                     (GraphWorkflowRunStatus.Cancelling, GraphWorkflowRunStatus.Running),
                     (GraphWorkflowRunStatus.Completed, GraphWorkflowRunStatus.Running),
                     (GraphWorkflowRunStatus.Failed, GraphWorkflowRunStatus.Running),
                     (GraphWorkflowRunStatus.Cancelled, GraphWorkflowRunStatus.Running)
                 })
        {
            AssertEx.False(GraphWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }
    }

    [Test]
    public void IsLegal_ForANodeRun_AcceptsTheDesignedEdgesAndRefusesTheRest()
    {
        foreach (var (from, to) in new[]
                 {
                     (GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Queued),

                     // The inline lane, which waits for no slot and so must not write a queue reason it has no token for.
                     (GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Running),
                     (GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Skipped),
                     (GraphWorkflowNodeRunStatus.Queued, GraphWorkflowNodeRunStatus.Running),
                     (GraphWorkflowNodeRunStatus.Queued, GraphWorkflowNodeRunStatus.Pending),
                     (GraphWorkflowNodeRunStatus.Running, GraphWorkflowNodeRunStatus.Succeeded),
                     (GraphWorkflowNodeRunStatus.Running, GraphWorkflowNodeRunStatus.WaitingForApproval),
                     (GraphWorkflowNodeRunStatus.Running, GraphWorkflowNodeRunStatus.Pending),
                     (GraphWorkflowNodeRunStatus.WaitingForApproval, GraphWorkflowNodeRunStatus.Succeeded),

                     // Retry in place, and the only way out of a terminal status.
                     (GraphWorkflowNodeRunStatus.Failed, GraphWorkflowNodeRunStatus.Pending)
                 })
        {
            AssertEx.True(GraphWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }

        foreach (var (from, to) in new[]
                 {
                     (GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Succeeded),
                     (GraphWorkflowNodeRunStatus.Queued, GraphWorkflowNodeRunStatus.Succeeded),
                     (GraphWorkflowNodeRunStatus.Queued, GraphWorkflowNodeRunStatus.WaitingForApproval),
                     (GraphWorkflowNodeRunStatus.Succeeded, GraphWorkflowNodeRunStatus.Running),
                     (GraphWorkflowNodeRunStatus.Cancelled, GraphWorkflowNodeRunStatus.Succeeded),

                     // Both answers SUCCEED a pause and route on the answer; skipping one would be walking past a
                     // decision instead of giving it.
                     (GraphWorkflowNodeRunStatus.WaitingForApproval, GraphWorkflowNodeRunStatus.Skipped)
                 })
        {
            AssertEx.False(GraphWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }
    }

    /// <summary>
    ///     Retry in place is the ONE edge out of a terminal status. The Dev Workflow module's other three belong to a
    ///     cross-node fix loop this runtime does not have, so a Succeeded, Skipped or Cancelled row here is an answer
    ///     nothing will ask again.
    /// </summary>
    [Test]
    public void IsLegal_ForANodeRun_PermitsFailedToPendingAndNoOtherTerminalExit()
    {
        foreach (var from in Enum.GetValues<GraphWorkflowNodeRunStatus>().Where(GraphWorkflowStateMachine.IsTerminal))
        {
            foreach (var to in Enum.GetValues<GraphWorkflowNodeRunStatus>())
            {
                var expected = from == GraphWorkflowNodeRunStatus.Failed && to == GraphWorkflowNodeRunStatus.Pending;

                AssertEx.Equal(expected, GraphWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
            }
        }
    }

    [Test]
    public void EnsureLegal_RejectsAnIllegalMoveThroughTheStoresRejectionChannel()
    {
        AssertEx.Contains(AssertEx.Throws<GraphWorkflowInvalidTransitionException>(() =>
                GraphWorkflowStateMachine.EnsureLegal(GraphWorkflowRunStatus.Completed, GraphWorkflowRunStatus.Running)).Message,
            "cannot move to Running");

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowInvalidTransitionException>(() =>
                GraphWorkflowStateMachine.EnsureLegal(GraphWorkflowNodeRunStatus.Pending, GraphWorkflowNodeRunStatus.Succeeded, "analyze")).Message,
            "Node run 'analyze' is Pending");
    }

    /// <summary>
    ///     Both answers, and only from an open pause. A status that is not decidable at all must advertise NOTHING, or
    ///     every button the panel draws answers "conflict".
    /// </summary>
    [Test]
    public void IsDecidable_AcceptsOnlyApproveAndRejectAndOnlyFromWaitingForApproval()
    {
        foreach (var status in Enum.GetValues<GraphWorkflowNodeRunStatus>())
        {
            foreach (var decision in Enum.GetValues<GraphWorkflowDecisionKind>())
            {
                AssertEx.Equal(status == GraphWorkflowNodeRunStatus.WaitingForApproval,
                    GraphWorkflowStateMachine.IsDecidable(status, decision),
                    $"{status} / {decision}");
            }
        }

        AssertEx.Equal("Approve, Reject", string.Join(", ", GraphWorkflowStateMachine.DecisionAnswers));
    }

    /// <summary>
    ///     The waiver apparatus is dropped because nothing in v1 can produce a waiver. Re-adding the member without its
    ///     producer would be dead code that reads as if it meant something, so it is a red rather than a comment.
    /// </summary>
    [Test]
    public void EdgeState_HasNoWaivedMember() =>
        AssertEx.Equal("Pending, Satisfied, Dead", string.Join(", ", Enum.GetNames<GraphWorkflowEdgeState>()));

    /// <summary>
    ///     One recomputation over a chain <c>node-0 → node-1 → …</c> as long as the statuses named, so the LAST of them
    ///     is the graph's one End node.
    /// </summary>
    private static GraphWorkflowRunStatus Recompute(GraphWorkflowRunStatus current, params GraphWorkflowNodeRunStatus[] nodeRuns) =>
        GraphWorkflowStateMachine.Recompute(current, Chain(nodeRuns.Length), [.. nodeRuns.Select((status, index) => NodeRun($"node-{index}", status))]).Status;

    /// <summary>A Start, <paramref name="length" /> minus two Agents, and an End, wired in a line.</summary>
    private static GraphWorkflowGraph Chain(int length)
    {
        var nodes = string.Join(", ",
            Enumerable.Range(0, length)
                      .Select(index => index switch
                      {
                          0 => """{ "key": "node-0", "kind": "Start" }""",
                          _ when index == length - 1 => $$"""{ "key": "node-{{index}}", "kind": "End", "config": { "outcome": "completed" } }""",
                          _ => $$"""{ "key": "node-{{index}}", "kind": "Agent", "config": { "instructions": "work" } }"""
                      }));
        var edges = string.Join(", ",
            Enumerable.Range(1, length - 1).Select(static index => $$"""{ "key": "edge-{{index}}", "from": "node-{{index - 1}}", "to": "node-{{index}}" }"""));
        return GraphWorkflowGraph.Parse($$"""{ "schemaVersion": 1, "nodes": [{{nodes}}], "edges": [{{edges}}] }""");
    }

    private static Dictionary<string, GraphWorkflowNodeRunSnapshot> ByKey(params GraphWorkflowNodeRunSnapshot[] nodeRuns) =>
        nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);

    private static GraphWorkflowNodeRunSnapshot NodeRun(string nodeKey,
        GraphWorkflowNodeRunStatus status,
        string? outputJson = null,
        GraphWorkflowNodeKind kind = GraphWorkflowNodeKind.Agent) =>
        new(Id: Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            NodeKey: nodeKey,
            Kind: kind,
            Status: status,
            Attempt: 1,
            PendingDecisionKind: null,
            DecisionOperationId: null,
            DecidedBySubject: null,
            FailureClass: GraphWorkflowFailureClass.None,
            Error: null,
            InputJson: null,
            OutputJson: outputJson,
            InvocationId: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            UpdatedAtUtc: 0);
}
