namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Where a work item stands. Written by the workflow runtime inside the transaction that transitions a run, never by
///     a client: a run start makes the item <c>Active</c>, a run waiting on a human makes it <c>Blocked</c>, and a
///     <em>failed</em> run also maps to <c>Blocked</c> — a failed run needs attention, it is not done.
/// </summary>
public enum DevWorkflowWorkItemStatus
{
    Draft,
    Active,
    Blocked,
    Completed,
    Cancelled
}

public enum DevWorkflowDefinitionSource
{
    Manual,
    Seeded
}

/// <summary>
///     A run's lifecycle. There is deliberately no <c>Interrupted</c>: runs auto-resume after a host restart and only
///     node-runs reconcile. <c>Pausing</c> and <c>Cancelling</c> exist because every runtime command is
///     fire-and-forget — the endpoint commits an intent and returns, so the UI has to be able to say "cancelling"
///     rather than claim a cancellation that has not landed.
/// </summary>
public enum DevWorkflowRunStatus
{
    Pending,
    Running,
    Pausing,
    Paused,
    WaitingForApproval,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
///     A node-run's lifecycle. <c>Queued</c> and <c>Running</c> are separate states on purpose: the node has one agent
///     slot, so a node-run admitted to the queue is not yet executing and the UI must not draw it as if it were.
///     <c>Skipped</c> covers both a gate's not-taken branch and a human <c>Skip</c> intervention.
/// </summary>
public enum DevWorkflowNodeRunStatus
{
    Pending,
    Queued,
    Running,
    WaitingForApproval,
    Blocked,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
///     The seven v1 node types. Start and End are implicit — an entry node is one with no inbound edges and a terminal
///     node one with no outbound edges — so neither is a member here; a client may draw synthetic anchors instead.
/// </summary>
public enum DevWorkflowNodeType
{
    Agent,
    Tool,
    DevTask,
    HumanGate,
    Gate,
    Parallel,
    Join
}

public enum DevWorkflowArtifactKind
{
    Research,
    Decision,
    Specification,
    Plan,
    TaskPackage,
    Patch,
    Report,
    Finding,
    ValidationReport,
    ReviewReport
}

/// <summary>
///     One enum for both halves of the same human act: a gate decision (<c>Approve</c>, <c>Reject</c>,
///     <c>RequestChanges</c>) and a retries-exhausted intervention (<c>Retry</c>, <c>Skip</c>, <c>Abandon</c>). They
///     share a table and an endpoint because they are the same thing — a human unblocking a node-run.
/// </summary>
public enum DevWorkflowDecisionKind
{
    Approve,
    Reject,
    RequestChanges,
    Retry,
    Skip,
    Abandon
}
