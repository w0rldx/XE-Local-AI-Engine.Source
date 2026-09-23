namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The closed <c>event_type</c> catalog of a graph workflow run — a contract, not a convenience.
/// </summary>
/// <remarks>
///     Extended by amendment and never silently: the run event feed is append-only and durable, so a token written
///     once is a token every later reader has to understand. It lives beside <see cref="IGraphWorkflowStore" /> for the
///     same reason <c>DevWorkflowEventTypes</c> lives beside its own store — the writer of the column and the
///     vocabulary of the column are one file apart. <see cref="RunWaiting" /> and the two <c>gate.*</c> tokens ship
///     unwritten, so the pause node that produces them adds behaviour rather than contract.
/// </remarks>
public static class GraphWorkflowEventTypes
{
    public const string RunCreated = "run.created";

    public const string RunStarted = "run.started";

    /// <summary>Written by the pause node.</summary>
    public const string RunWaiting = "run.waiting";

    public const string RunCompleted = "run.completed";

    public const string RunFailed = "run.failed";

    public const string RunCancelled = "run.cancelled";

    public const string NodeQueued = "node.queued";

    public const string NodeStarted = "node.started";

    public const string NodeCompleted = "node.completed";

    public const string NodeFailed = "node.failed";

    public const string NodeSkipped = "node.skipped";

    public const string NodeCancelled = "node.cancelled";

    public const string NodeInterrupted = "node.interrupted";

    /// <summary>
    ///     A retry in place. The row clears the failure fields it is re-attempting because of, so this event's detail is
    ///     the only place that failure survives.
    /// </summary>
    public const string NodeRetried = "node.retried";

    /// <summary>Written by the pause node.</summary>
    public const string GateRequested = "gate.requested";

    /// <summary>Written by the pause node.</summary>
    public const string GateDecided = "gate.decided";

    /// <summary>
    ///     A succeeded node's result became a chat message in the run's bound conversation. Amendment 2026-09-23 (Chat
    ///     Workflows S1): written by the dispatcher's publish outbox pass, detail <c>{ messageId }</c>.
    /// </summary>
    public const string NodePublished = "node.published";
}
