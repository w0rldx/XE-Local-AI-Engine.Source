namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A run and its node runs in one read, composed from the store's snapshots rather than re-declaring their fields.
/// </summary>
/// <remarks>
///     The GRAPH is deliberately not in it: a caller that needs nodes and edges reads the run's pinned blob, and the
///     runtime already holds that parsed.
/// </remarks>
public sealed class GraphWorkflowRunDetail
{
    public required GraphWorkflowRunSnapshot Run { get; init; }

    public required IReadOnlyList<GraphWorkflowNodeRunSnapshot> NodeRuns { get; init; }
}

/// <summary>
///     One page of a run's event log. <see cref="ReplayTruncated" /> is OBSERVED rather than inferred: the page is read
///     one row over its limit, so a client that fell behind is told it was cut off instead of quietly handed a partial
///     log it would mistake for the whole one.
/// </summary>
public sealed class GraphWorkflowRunEventPage
{
    public required IReadOnlyList<GraphWorkflowRunEventSnapshot> Events { get; init; }

    public required long LastSeq { get; init; }

    public required bool ReplayTruncated { get; init; }
}

/// <summary>
///     What a decision left behind: the answer that now stands, and the CURRENT statuses of the run and of the pause
///     it answered.
/// </summary>
/// <remarks>
///     Current, not predicted — what follows a decision is the dispatcher's work on its own clock, so a result
///     promising <c>Running</c> would be describing a tick that has not happened.
/// </remarks>
public sealed class GraphWorkflowDecisionResult
{
    public required GraphWorkflowDecisionKind Decision { get; init; }

    public required GraphWorkflowRunStatus RunStatus { get; init; }

    public required GraphWorkflowNodeRunStatus NodeRunStatus { get; init; }
}

/// <summary>
///     Both ways a run command can lose, under one type because from the client's side they are one story: you are
///     acting on a version of this run that no longer exists.
/// </summary>
/// <remarks>
///     A stale <c>definitionVersion</c> at start, and a cancel of a run that has already finished. Maps to a 409
///     through <c>ConflictExceptionHandler</c>.
/// </remarks>
public sealed class GraphWorkflowRunConflictException : InvalidOperationException
{
    public GraphWorkflowRunConflictException(string message) : base(message)
    {
    }
}

/// <summary>A steer of a node run that already holds <c>MaxSteersPerNode</c> entries. Maps to 409 <c>GraphWorkflowSteerLimitReached</c>.</summary>
public sealed class GraphWorkflowSteerLimitReachedException : InvalidOperationException
{
    public GraphWorkflowSteerLimitReachedException(string message) : base(message)
    {
    }
}

/// <summary>The chat conversation a run starts bound to, and the user message that started it.</summary>
public sealed class GraphWorkflowRunBinding
{
    public required Guid ConversationId { get; init; }

    public required Guid TriggerMessageId { get; init; }
}

/// <summary>Every way a caller changes or reads a graph workflow run.</summary>
/// <remarks>
///     <b>The commands are fire-and-forget.</b> Each validates, commits a durable intent, signals the dispatcher and
///     returns the CURRENT state — which legitimately reads <c>Pending</c> or <c>Cancelling</c>. Nothing here waits
///     for the runtime to act, which is what keeps the HTTP path off the node's one invocation slot.
///     <see cref="StartAsync" /> is idempotent on a caller-minted request id: the same id always answers with the
///     same run, so an integration that never saw the first answer retries without risking a second run.
/// </remarks>
public interface IGraphWorkflowRunService
{
    /// <summary>
    ///     Starts a run of <paramref name="definitionId" />, pinning the definition's graph and creating a
    ///     <c>Pending</c> node run for every node in it.
    /// </summary>
    /// <param name="requestId">The caller-minted idempotency key. Non-empty, and unique across every run this node has.</param>
    /// <param name="definitionVersion">
    ///     The version the caller believed it was starting. A stale one answers
    ///     <see cref="GraphWorkflowRunConflictException" /> rather than running a graph the caller never saw; null skips
    ///     the check.
    /// </param>
    Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same start, bound to a chat conversation. A conversation that already has a live bound run answers
    ///     <see cref="GraphWorkflowRunBusyException" />; the database's partial unique index is the lock.
    /// </summary>
    Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        GraphWorkflowRunBinding? binding,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the intent to cancel and signals. The run reads <c>Cancelling</c> until the dispatcher has drained
    ///     whatever is in flight; a run that is already terminal answers <see cref="GraphWorkflowRunConflictException" />.
    /// </summary>
    Task<GraphWorkflowRunDetail> CancelAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<GraphWorkflowRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status,
        int limit,
        CancellationToken cancellationToken = default);

    Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default);

    /// <summary>Answers the pause at <paramref name="nodeKey" />, signals, and reports what the rows now say.</summary>
    /// <remarks>
    ///     <b>Idempotent on <paramref name="operationId" />.</b> The same id twice answers with the decision already
    ///     recorded; naming a different answer, person or pause of the same run is a caller bug and answers
    ///     <see cref="GraphWorkflowGateAlreadyDecidedException" />. Comment and payload are deliberately not compared —
    ///     free text around the act, not the act. BOTH answers succeed the node run: a rejection routes through an
    ///     out-edge matching nothing rather than a node failure, and one with nowhere to go strands the run as <c>Cancelled</c>/<c>GateRejected</c> rather than being refused here.
    /// </remarks>
    Task<GraphWorkflowDecisionResult> DecideAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Commits an operator steer of the queued or running Agent/LLM call node at <paramref name="nodeKey" /> of a
    ///     chat-bound run, signals, and answers the run as it now reads.
    /// </summary>
    /// <remarks>
    ///     Intent only: the dispatcher tick resets the row to <c>Pending</c> on the same attempt, or records the steer
    ///     as ignored when the row settled first. <b>Idempotent on <paramref name="operationId" /></b> within the
    ///     row's entries; the same id with a different message or person is a
    ///     <see cref="GraphWorkflowRunConflictException" />, and a row at the cap a <see cref="GraphWorkflowSteerLimitReachedException" />.
    /// </remarks>
    Task<GraphWorkflowRunDetail> SteerAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        string message,
        string? steeredBySubject,
        CancellationToken cancellationToken = default);

    /// <summary><paramref name="afterSeq" /> is an EXCLUSIVE lower bound; the page is capped at the configured replay limit.</summary>
    Task<GraphWorkflowRunEventPage> ListEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken = default);
}
