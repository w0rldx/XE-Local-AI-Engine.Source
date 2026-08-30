namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What an inbound edge says about whether its target may proceed.</summary>
internal enum DevWorkflowEdgeState
{
    /// <summary>The source has not settled, or has settled and this edge is still to be judged against a live sibling.</summary>
    Pending,

    /// <summary>The source succeeded and this edge's condition (if any) fired.</summary>
    Satisfied,

    /// <summary>The source settled in a way this edge can never fire on. Nothing downstream of it will ever come.</summary>
    Dead
}

/// <summary>What the dispatcher should do with a <c>Pending</c> node run this tick.</summary>
internal enum DevWorkflowNodeAdmission
{
    /// <summary>An inbound edge is still undecided. Leave it alone.</summary>
    Wait,

    /// <summary>Its dependencies are satisfied; queue it.</summary>
    Eligible,

    /// <summary>Every path into it is dead. It will never run, and its own out-edges die with it.</summary>
    Skip
}

/// <summary>
///     The run and node-run state machines, as pure functions over persisted rows and the parsed graph.
///     <para>
///         The store deliberately does not judge transitions — it provides the rejection channel and enforces only what
///         the database can. These functions are therefore the only guard, and being free of I/O is what lets the whole
///         truth table be tested without a database.
///     </para>
/// </summary>
internal static class DevWorkflowStateMachine
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The output document a human gate produces for one answer — the document its out-edge conditions are then
    ///     evaluated against. Written in ONE place because two callers ask questions of it: the dispatcher, when it
    ///     routes an answer that has landed, and the API, when it tells the operator in advance whether a rejection has
    ///     anywhere to go. A second spelling of this shape would make those two disagree in exactly the case that
    ///     matters.
    /// </summary>
    public static string GateOutputJson(DevWorkflowDecisionKind decision) =>
        JsonSerializer.Serialize(new GateOutput(DevWorkflowNodeOutputStatuses.Succeeded, decision.ToString()), JsonOptions);

    /// <summary>A node run nothing further will happen to on its own.</summary>
    public static bool IsTerminal(DevWorkflowNodeRunStatus status) =>
        status is DevWorkflowNodeRunStatus.Succeeded
            or DevWorkflowNodeRunStatus.Failed
            or DevWorkflowNodeRunStatus.Skipped
            or DevWorkflowNodeRunStatus.Cancelled;

    /// <summary>
    ///     A node run the run is still waiting on — including the two human-wait states, which is what keeps a run from
    ///     completing behind an unanswered gate.
    /// </summary>
    public static bool IsLive(DevWorkflowNodeRunStatus status) =>
        !IsTerminal(status);

    public static bool IsTerminal(DevWorkflowRunStatus status) =>
        status is DevWorkflowRunStatus.Completed or DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.Cancelled;

    /// <summary>
    ///     Whether an inbound edge lets its target through, given the source node run — or <see langword="null" /> when
    ///     the source has not been materialized yet, which is itself a wait rather than a refusal.
    /// </summary>
    public static DevWorkflowEdgeState EdgeState(DevWorkflowGraphEdge edge, DevWorkflowNodeRunSnapshot? source)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (source is null || !IsTerminal(source.Status))
        {
            return DevWorkflowEdgeState.Pending;
        }

        // Failed, Cancelled and Skipped sources kill every out-edge: none of them produced the output a condition would
        // read, and treating "no output" as a passing condition is how a run routes on evidence it never had.
        if (source.Status != DevWorkflowNodeRunStatus.Succeeded)
        {
            return DevWorkflowEdgeState.Dead;
        }

        return DevWorkflowCondition.Evaluate(edge.Condition, ParseOutput(source.OutputJson))
            ? DevWorkflowEdgeState.Satisfied
            : DevWorkflowEdgeState.Dead;
    }

    /// <summary>
    ///     Whether a <c>Pending</c> node run may be queued, must be skipped, or is still waiting.
    ///     <para>
    ///         <c>All</c> over ZERO inbound edges is vacuously satisfied, and that is load-bearing rather than pedantic:
    ///         it is how an entry node becomes eligible at all (Start is implicit, so an entry node is one with no
    ///         inbound edges), and it is what lets a decomposition that produced no tasks finish — the join is left with
    ///         no inbound edges, proceeds, and the run completes. The opposite reading hangs such a run forever.
    ///     </para>
    /// </summary>
    public static DevWorkflowNodeAdmission Admission(DevWorkflowGraphNode node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var states = graph.InboundEdges(node.NodeKey)
                          .Select(edge => EdgeState(edge, nodeRunsByKey.GetValueOrDefault(edge.From)))
                          .ToList();

        if (node.JoinPolicy == DevWorkflowJoinPolicy.All)
        {
            if (states.Contains(DevWorkflowEdgeState.Dead))
            {
                return DevWorkflowNodeAdmission.Skip;
            }

            return states.Contains(DevWorkflowEdgeState.Pending) ? DevWorkflowNodeAdmission.Wait : DevWorkflowNodeAdmission.Eligible;
        }

        // Any: one satisfied branch is enough, but only once no sibling could still satisfy one — otherwise the join
        // would fire on whichever branch happened to land first.
        if (states.Contains(DevWorkflowEdgeState.Pending))
        {
            return DevWorkflowNodeAdmission.Wait;
        }

        return states.Contains(DevWorkflowEdgeState.Satisfied) ? DevWorkflowNodeAdmission.Eligible : DevWorkflowNodeAdmission.Skip;
    }

    /// <summary>
    ///     The status a run should hold given its node runs, recomputed from scratch at the end of every tick rather than
    ///     accumulated. It is denormalized on purpose so a reader can answer "what is this run doing" without a join.
    ///     <para>
    ///         The <c>-ing</c> statuses are not decided here: they are intents a command wrote, and only the drain that
    ///         settles them may clear them. Terminal runs are likewise left alone.
    ///     </para>
    /// </summary>
    public static DevWorkflowRunStatus Recompute(DevWorkflowRunStatus current, IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        if (IsTerminal(current) || current is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling or DevWorkflowRunStatus.Paused)
        {
            return current;
        }

        if (nodeRuns.Any(nodeRun => IsLive(nodeRun.Status)))
        {
            if (nodeRuns.Any(nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running))
            {
                return DevWorkflowRunStatus.Running;
            }

            // Blocked and WaitingForApproval both mean "a human has to act", and they outrank Pending deliberately:
            // every node run of a graph exists from the moment the run starts, so there are almost always Pending rows
            // waiting on a branch that has not settled. Reading those as Running would report a run blocked on an
            // unanswered gate as busy, which is the one thing the two statuses exist to tell apart.
            return nodeRuns.Any(nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                ? DevWorkflowRunStatus.WaitingForApproval
                : DevWorkflowRunStatus.Running;
        }

        // A run with no node runs at all has not been materialized yet; it is still Pending, not complete.
        if (nodeRuns.Count == 0)
        {
            return current;
        }

        // Skipped and Cancelled node runs are terminal and do not block completion. A run whose every node was skipped
        // completes: every branch condition was false, which is a real outcome, and the event log says which.
        return nodeRuns.Any(nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Failed)
            ? DevWorkflowRunStatus.Failed
            : DevWorkflowRunStatus.Completed;
    }

    /// <summary>
    ///     Where a run's status and its node runs leave the work item. Written inside the same transaction as the run
    ///     transition, never derived on read and never client-writable, so the two can never disagree.
    ///     <para>
    ///         ANY blocked node run blocks the work item, even while the run itself reads <c>Running</c> because a
    ///         sibling is still working. Reading only the run status would leave a work item Active with a node run
    ///         nobody is coming to unblock — the list page's whole job is to surface exactly that.
    ///     </para>
    /// </summary>
    public static DevWorkflowWorkItemStatus WorkItemStatusFor(DevWorkflowRunStatus runStatus, IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        return runStatus switch
        {
            DevWorkflowRunStatus.Completed => DevWorkflowWorkItemStatus.Completed,
            DevWorkflowRunStatus.Cancelled => DevWorkflowWorkItemStatus.Cancelled,

            // A failed run needs attention; it is not done. Same for a run waiting on a human.
            DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.WaitingForApproval => DevWorkflowWorkItemStatus.Blocked,
            _ when nodeRuns.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Blocked) => DevWorkflowWorkItemStatus.Blocked,
            _ => DevWorkflowWorkItemStatus.Active
        };
    }

    /// <summary>
    ///     Where a human's answer leaves the node run it answers.
    ///     <para>
    ///         A gate answer always SUCCEEDS the gate, whichever of the three it is: the answer is the node's output,
    ///         and routing on it is the edges' job. A rejection reaches the run through an out-edge that matches
    ///         nothing, not through a node failure — which is why <c>Reject</c> and <c>Approve</c> land in the same
    ///         place here and part company in the graph.
    ///     </para>
    ///     <para>
    ///         Shared by the decision endpoint and the dispatcher so the two cannot disagree about which answers a row
    ///         in a given status can take: the endpoint refuses the rest with a conflict, and the dispatcher keeps its
    ///         own guard for a decision recorded around it.
    ///     </para>
    /// </summary>
    public static DevWorkflowNodeRunStatus TargetFor(DevWorkflowDecisionKind decision) =>
        decision switch
        {
            DevWorkflowDecisionKind.Approve or DevWorkflowDecisionKind.Reject or DevWorkflowDecisionKind.RequestChanges => DevWorkflowNodeRunStatus.Succeeded,

            // Forced: a human retry ignores MaxAttempts, and only the run-wide attempt budget still bounds it.
            DevWorkflowDecisionKind.Retry => DevWorkflowNodeRunStatus.Pending,
            DevWorkflowDecisionKind.Skip => DevWorkflowNodeRunStatus.Skipped,
            _ => DevWorkflowNodeRunStatus.Failed
        };

    /// <summary>
    ///     Where a node-run transition about to be written leaves the work item, so the move can carry it in its own
    ///     transaction.
    ///     <para>
    ///         Needed because the run status often does not change when a node run does — a node blocking while a
    ///         sibling still works leaves the run <c>Running</c> — and the end-of-tick recomputation writes nothing when
    ///         the run status is unchanged. Without this the work item would keep reading <c>Active</c> with a node run
    ///         nobody is coming to unblock, which is the one thing the list page exists to surface.
    ///     </para>
    /// </summary>
    public static DevWorkflowWorkItemStatus WorkItemStatusAfter(DevWorkflowRunStatus runStatus,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        Guid nodeRunId,
        DevWorkflowNodeRunStatus target)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        var projected = nodeRuns.Select(nodeRun => nodeRun.Id == nodeRunId
            ? nodeRun with
            {
                Status = target
            }
            : nodeRun).ToList();
        return WorkItemStatusFor(Recompute(runStatus, projected), projected);
    }

    /// <summary>
    ///     The run transition table. Every terminal is reached through a drain (<c>Pausing</c>/<c>Cancelling</c>) or
    ///     through the "nothing is live any more" recomputation — there is deliberately no direct edge from a
    ///     non-terminal status to <c>Cancelled</c>, because writing one would strand the run's live node runs under a
    ///     terminal run that is never advanced again, leaking the slots their executors hold.
    /// </summary>
    public static bool IsLegal(DevWorkflowRunStatus from, DevWorkflowRunStatus to) =>
        from switch
        {
            DevWorkflowRunStatus.Pending => to is DevWorkflowRunStatus.Running or DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Running => to is DevWorkflowRunStatus.WaitingForApproval
                or DevWorkflowRunStatus.Pausing
                or DevWorkflowRunStatus.Cancelling
                or DevWorkflowRunStatus.Completed
                or DevWorkflowRunStatus.Failed,
            DevWorkflowRunStatus.WaitingForApproval => to is DevWorkflowRunStatus.Running
                or DevWorkflowRunStatus.Pausing
                or DevWorkflowRunStatus.Cancelling
                or DevWorkflowRunStatus.Completed
                or DevWorkflowRunStatus.Failed,
            DevWorkflowRunStatus.Pausing => to is DevWorkflowRunStatus.Paused or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Paused => to is DevWorkflowRunStatus.Running or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Cancelling => to is DevWorkflowRunStatus.Cancelled,
            _ => false
        };

    /// <summary>
    ///     The node-run transition table.
    ///     <para>
    ///         <c>Running → Pending</c> and <c>Queued → Pending</c> carry two different meanings that need no distinct
    ///         edge: a retry scheduled after a retryable failure, and a collapse after the host restarted under the
    ///         node run. Both re-derive the same way, which is why the row is cleaned rather than annotated.
    ///     </para>
    ///     <para>
    ///         The four edges OUT of a terminal status back to <c>Pending</c> belong to the cross-node fix loop (X9) and
    ///         to nothing else: when a failure routes to an upstream node, every node run downstream of that node has to
    ///         re-run against the new implementation, and those rows are settled by definition — the whole point is that
    ///         they already produced an answer to a question that is being asked again. A <c>Succeeded</c> row left
    ///         alone would be a stale result masquerading as a current one, which is the outcome that rule exists to
    ///         prevent. Nothing else may write them: the decision path only ever moves a row out of
    ///         <c>WaitingForApproval</c> or <c>Blocked</c>, and every executor settles forwards.
    ///     </para>
    /// </summary>
    public static bool IsLegal(DevWorkflowNodeRunStatus from, DevWorkflowNodeRunStatus to) =>
        from switch
        {
            // Straight to Running is the inline lane: a gate, a join or a fan-out waits for no slot, so routing it
            // through Queued would write a queue reason there is no honest token for.
            DevWorkflowNodeRunStatus.Pending => to is DevWorkflowNodeRunStatus.Queued
                or DevWorkflowNodeRunStatus.Running
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Cancelled,
            DevWorkflowNodeRunStatus.Queued => to is DevWorkflowNodeRunStatus.Running
                or DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Cancelled,
            DevWorkflowNodeRunStatus.Running => to is DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.WaitingForApproval
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Cancelled,
            // NOT Skipped (X3): a gate's three answers all SUCCEED it and route on the answer, while the three
            // interventions belong to Blocked. Skipping an open gate would be an operator walking past an approval
            // instead of giving one — the one thing a gate exists to make impossible. The only other moves are the
            // drain's cancel and the fix loop's reset, and the reset is the OPPOSITE of walking past it: an open gate
            // downstream of a node being re-attempted is being asked to approve work that is being replaced, so it is
            // re-asked from the start of a new attempt rather than answered about the old one.
            DevWorkflowNodeRunStatus.WaitingForApproval => to is DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Cancelled
                or DevWorkflowNodeRunStatus.Pending,

            // The intervention answers: Retry re-attempts, Skip routes around, Abandon gives up for good.
            DevWorkflowNodeRunStatus.Blocked => to is DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Cancelled,

            // The fix loop's reset, and only it. See the remarks above.
            DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Cancelled => to is DevWorkflowNodeRunStatus.Pending,
            _ => false
        };

    public static void EnsureLegal(DevWorkflowRunStatus from, DevWorkflowRunStatus to)
    {
        if (!IsLegal(from, to))
        {
            throw new DevWorkflowInvalidTransitionException($"A development workflow run in {from} cannot move to {to}.");
        }
    }

    public static void EnsureLegal(DevWorkflowNodeRunStatus from, DevWorkflowNodeRunStatus to, string nodeKey)
    {
        if (!IsLegal(from, to))
        {
            throw new DevWorkflowInvalidTransitionException($"Node run '{nodeKey}' is {from} and cannot move to {to}.");
        }
    }

    /// <summary>
    ///     A node run's output document, or <see langword="null" /> when it has none or the stored text is not an object.
    ///     Unreadable output is treated as absent so conditions fail closed rather than throwing mid-tick.
    /// </summary>
    private static JsonElement? ParseOutput(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GateOutput(string Status, string Decision);
}
