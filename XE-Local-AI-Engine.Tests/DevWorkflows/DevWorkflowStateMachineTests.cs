namespace XE_Local_AI_Engine.Tests.DevWorkflows;

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
            AssertEx.Equal(DevWorkflowEdgeState.Pending, DevWorkflowStateMachine.EdgeState(edge, NodeRun("research", status)), status.ToString());
        }

        AssertEx.Equal(DevWorkflowEdgeState.Pending,
            DevWorkflowStateMachine.EdgeState(edge, source: null),
            "a source that has not been materialized yet is a wait, not a refusal.");
    }

    [Test]
    [Arguments(DevWorkflowNodeRunStatus.Failed)]
    [Arguments(DevWorkflowNodeRunStatus.Skipped)]
    [Arguments(DevWorkflowNodeRunStatus.Cancelled)]
    public void EdgeState_WhenTheSourceSettledWithoutSucceeding_IsDead(DevWorkflowNodeRunStatus status)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        AssertEx.Equal(DevWorkflowEdgeState.Dead, DevWorkflowStateMachine.EdgeState(graph.OutboundEdges("research")[0], NodeRun("research", status)));
    }

    [Test]
    public void EdgeState_OnASucceededSource_FollowsTheEdgeCondition()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var ship = graph.OutboundEdges("approve").Single(edge => edge.To == "ship");
        var revise = graph.OutboundEdges("approve").Single(edge => edge.To == "revise");
        var approved = NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, """{"decision":"Approve"}""");

        AssertEx.Equal(DevWorkflowEdgeState.Satisfied, DevWorkflowStateMachine.EdgeState(ship, approved));
        AssertEx.Equal(DevWorkflowEdgeState.Dead, DevWorkflowStateMachine.EdgeState(revise, approved));
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
            DevWorkflowStateMachine.EdgeState(ship, NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, outputJson)));
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
    public void Admission_UnderAll_WaitsForEveryBranchAndSkipsOnAnyDeadOne()
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

        AssertEx.Equal(DevWorkflowNodeAdmission.Skip,
            DevWorkflowStateMachine.Admission(done,
                graph,
                ByKey(NodeRun("ship", DevWorkflowNodeRunStatus.Skipped), NodeRun("revise", DevWorkflowNodeRunStatus.Skipped))),
            "with every branch dead, even an Any join has nothing to carry.");
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

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Cancelled),
            "skipped and cancelled node runs are terminal and do not block completion.");

        AssertEx.Equal(DevWorkflowRunStatus.Completed,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Skipped),
            "a run whose every branch condition was false is a real outcome, and the event log says which.");

        AssertEx.Equal(DevWorkflowRunStatus.Failed,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Failed));

        AssertEx.Equal(DevWorkflowRunStatus.Running,
            Recompute(DevWorkflowRunStatus.Running, DevWorkflowNodeRunStatus.Failed, DevWorkflowNodeRunStatus.Running),
            "a failure with a live sibling does not end the run — the sibling still has to settle.");
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
            DevWorkflowStateMachine.Recompute(DevWorkflowRunStatus.Pending, []),
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
        AssertEx.Equal(expected, DevWorkflowStateMachine.WorkItemStatusFor(runStatus));

    /// <summary>
    ///     The one edge that must never exist. A terminal written without draining strands the run's live node runs
    ///     under a run nothing advances again, and their executors' slots leak for the process lifetime.
    /// </summary>
    [Test]
    public void IsLegal_ForARun_ReachesCancelledOnlyThroughCancelling()
    {
        foreach (var from in Enum.GetValues<DevWorkflowRunStatus>().Where(static status => status != DevWorkflowRunStatus.Cancelling))
        {
            AssertEx.False(DevWorkflowStateMachine.IsLegal(from, DevWorkflowRunStatus.Cancelled), $"{from} → Cancelled must go through Cancelling.");
        }

        AssertEx.True(DevWorkflowStateMachine.IsLegal(DevWorkflowRunStatus.Cancelling, DevWorkflowRunStatus.Cancelled));
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
                     (DevWorkflowNodeRunStatus.Blocked, DevWorkflowNodeRunStatus.Failed)
                 })
        {
            AssertEx.True(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }

        foreach (var (from, to) in new[]
                 {
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Running),
                     (DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Succeeded),
                     (DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.WaitingForApproval),
                     (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Failed, DevWorkflowNodeRunStatus.Pending),
                     (DevWorkflowNodeRunStatus.Skipped, DevWorkflowNodeRunStatus.Queued),
                     (DevWorkflowNodeRunStatus.Cancelled, DevWorkflowNodeRunStatus.Pending)
                 })
        {
            AssertEx.False(DevWorkflowStateMachine.IsLegal(from, to), $"{from} → {to}");
        }
    }

    [Test]
    public void EnsureLegal_RejectsAnIllegalMoveThroughTheStoresRejectionChannel()
    {
        AssertEx.Contains(
            AssertEx.Throws<DevWorkflowInvalidTransitionException>(() =>
                DevWorkflowStateMachine.EnsureLegal(DevWorkflowRunStatus.Completed, DevWorkflowRunStatus.Running)).Message,
            "cannot move to Running");

        AssertEx.Contains(
            AssertEx.Throws<DevWorkflowInvalidTransitionException>(() =>
                DevWorkflowStateMachine.EnsureLegal(DevWorkflowNodeRunStatus.Pending, DevWorkflowNodeRunStatus.Running, "plan")).Message,
            "Node run 'plan' is Pending");
    }

    private static DevWorkflowRunStatus Recompute(DevWorkflowRunStatus current, params DevWorkflowNodeRunStatus[] nodeRuns) =>
        DevWorkflowStateMachine.Recompute(current, [.. nodeRuns.Select((status, index) => NodeRun($"node-{index}", status))]);

    private static Dictionary<string, DevWorkflowNodeRunSnapshot> ByKey(params DevWorkflowNodeRunSnapshot[] nodeRuns) =>
        nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);

    private static DevWorkflowNodeRunSnapshot NodeRun(string nodeKey, DevWorkflowNodeRunStatus status, string? outputJson = null) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            nodeKey,
            DevWorkflowNodeType.Agent,
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
