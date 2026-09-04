namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The closed v1 node vocabulary of a Graph Workflow. Unlike the Dev Workflow module, <c>Start</c> and <c>End</c>
///     are explicit nodes: a definition has exactly one <c>Start</c> and at least one <c>End</c>, and the parser refuses
///     any other kind. Widening this set is a reviewed schema change.
/// </summary>
public enum GraphWorkflowNodeKind
{
    Start,
    Agent,
    Tool,
    Condition,
    Parallel,
    Join,
    Pause,
    End
}

/// <summary>
///     How a node waits on its inbound edges. A property of every node (default <c>All</c>), not only of
///     <c>Join</c> nodes — reading it off <c>Join</c> alone is the documented trap.
/// </summary>
public enum GraphWorkflowJoinPolicy
{
    All,
    Any
}

/// <summary>
///     A run's lifecycle. No <c>Interrupted</c>: runs auto-resume after a host restart and only node runs reconcile.
///     <c>Cancelling</c> exists because cancel is fire-and-forget — the endpoint commits an intent and returns.
/// </summary>
public enum GraphWorkflowRunStatus
{
    Pending,
    Running,
    WaitingForApproval,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
///     A node run's lifecycle. <c>Queued</c> and <c>Running</c> are separate on purpose: an admitted node run is not yet
///     executing. There is no <c>Blocked</c> — v1 has no retry routing, so no retries-exhausted intervention state.
/// </summary>
public enum GraphWorkflowNodeRunStatus
{
    Pending,
    Queued,
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>The answers a <c>Pause</c> node accepts. Both land the node run on <c>Succeeded</c>; they part company in the graph.</summary>
public enum GraphWorkflowDecisionKind
{
    Approve,
    Reject
}

/// <summary>
///     Why a run or node run ended the way it did. Persisted as text (<c>HasConversion&lt;string&gt;</c>) so the column
///     matches the Dev Workflow shape while C# and the wire stay typed.
/// </summary>
public enum GraphWorkflowFailureClass
{
    None,
    NodeFailed,
    Timeout,
    AttemptsExhausted,
    OutputTooLarge,
    GateRejected,
    ValidationFailed,
    Cancelled,
    Interrupted
}
