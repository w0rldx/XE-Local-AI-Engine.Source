namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A run and its node runs in one read, composed from the store's snapshots rather than re-declaring their fields.
///     <para>
///         The GRAPH is deliberately not in it: a caller that needs nodes and edges reads the run's pinned blob, and the
///         runtime already holds that parsed.
///     </para>
/// </summary>
public sealed record GraphWorkflowRunDetail(GraphWorkflowRunSnapshot Run, IReadOnlyList<GraphWorkflowNodeRunSnapshot> NodeRuns);

/// <summary>
///     One page of a run's event log. <see cref="ReplayTruncated" /> is OBSERVED rather than inferred: the page is read
///     one row over its limit, so a client that fell behind is told it was cut off instead of quietly handed a partial
///     log it would mistake for the whole one.
/// </summary>
public sealed record GraphWorkflowRunEventPage(IReadOnlyList<GraphWorkflowRunEventSnapshot> Events, long LastSeq, bool ReplayTruncated);

/// <summary>
///     What a decision left behind: the answer that now stands, and the CURRENT statuses of the run and of the pause it
///     answered. Current, not predicted — what follows a decision is the dispatcher's work on its own clock, so a
///     result promising <c>Running</c> would be describing a tick that has not happened.
/// </summary>
public sealed record GraphWorkflowDecisionResult(
    GraphWorkflowDecisionKind Decision,
    GraphWorkflowRunStatus RunStatus,
    GraphWorkflowNodeRunStatus NodeRunStatus);

/// <summary>
///     Both ways a run command can lose, under one type because from the client's side they are one story — you are
///     acting on a version of this run that no longer exists: a stale <c>definitionVersion</c> at start, and a cancel of
///     a run that has already finished. Maps to a 409 through <c>ConflictExceptionHandler</c>.
/// </summary>
public sealed class GraphWorkflowRunConflictException(string message) : InvalidOperationException(message);

/// <summary>
///     Every way a caller changes or reads a graph workflow run.
///     <para>
///         <b>The commands are fire-and-forget.</b> Each validates, commits a durable intent, signals the dispatcher and
///         returns the CURRENT state — which legitimately reads <c>Pending</c> or <c>Cancelling</c>. Nothing here waits
///         for the runtime to act, which is what keeps the HTTP path off the node's one invocation slot.
///     </para>
///     <para>
///         <b><see cref="StartAsync" /> is idempotent on a caller-minted request id.</b> The same id always answers with
///         the same run, so an integration that never saw the first answer retries without risking a second run.
///     </para>
/// </summary>
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
    ///     Records the intent to cancel and signals. The run reads <c>Cancelling</c> until the dispatcher has drained
    ///     whatever is in flight; a run that is already terminal answers <see cref="GraphWorkflowRunConflictException" />.
    /// </summary>
    Task<GraphWorkflowRunDetail> CancelAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<GraphWorkflowRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status,
        int limit,
        CancellationToken cancellationToken = default);

    Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Answers the pause at <paramref name="nodeKey" />, signals, and reports what the rows now say.
    ///     <para>
    ///         <b>Idempotent on <paramref name="operationId" />.</b> The same id sent twice answers with the decision it
    ///         already recorded rather than deciding again; the same id naming a different answer, a different person or
    ///         a different pause of the same run is a caller bug and answers
    ///         <see cref="GraphWorkflowGateAlreadyDecidedException" />. Comment and payload are deliberately not
    ///         compared — they are the free text around the act rather than the act.
    ///     </para>
    ///     <para>
    ///         BOTH answers succeed the node run. A rejection reaches the run through an out-edge that matches nothing,
    ///         not through a node failure, and a rejection with nowhere to go strands the run as
    ///         <c>Cancelled</c>/<c>GateRejected</c> — an honest outcome rather than an error to refuse here.
    ///     </para>
    /// </summary>
    Task<GraphWorkflowDecisionResult> DecideAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default);

    /// <summary><paramref name="afterSeq" /> is an EXCLUSIVE lower bound; the page is capped at the configured replay limit.</summary>
    Task<GraphWorkflowRunEventPage> ListEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken = default);
}
