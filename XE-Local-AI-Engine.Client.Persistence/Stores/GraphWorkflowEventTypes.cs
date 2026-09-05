namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The closed <c>event_type</c> catalog of a graph workflow run — a contract, not a convenience.
///     <para>
///         Sixteen tokens, extended by amendment and never silently: the run event feed is append-only and durable, so
///         a token written once is a token every later reader has to understand. It lives beside
///         <see cref="IGraphWorkflowStore" /> for the same reason <c>DevWorkflowEventTypes</c> lives beside its own
///         store — the writer of the column and the vocabulary of the column are one file apart.
///     </para>
///     <para>
///         <see cref="RunWaiting" /> and the two <c>gate.*</c> tokens ship unwritten: the pause node that produces them
///         lands in the next slice, and shipping the vocabulary now means that slice adds behaviour rather than
///         contract.
///     </para>
/// </summary>
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
}
