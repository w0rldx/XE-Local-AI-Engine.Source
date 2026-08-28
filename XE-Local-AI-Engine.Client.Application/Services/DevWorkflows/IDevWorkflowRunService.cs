namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A run and its node runs in one read, composed from the store's snapshots rather than re-declaring their fields.
///     <para>
///         The GRAPH is deliberately not in it: a caller that needs nodes and edges decrypts the run's pinned blob,
///         which the runtime already holds parsed and has no reason to re-serialize.
///     </para>
/// </summary>
/// <param name="PendingDecisionCount">Node runs waiting on a human — a gate's approval and an exhausted node's intervention alike.</param>
/// <param name="BlockingGateNodeRunId">The oldest node run waiting on a gate answer, in sequence order, if there is one.</param>
public sealed record DevWorkflowRunDetail(
    DevWorkflowRunSnapshot Run,
    IReadOnlyList<DevWorkflowNodeRunSnapshot> NodeRuns,
    int PendingDecisionCount,
    Guid? BlockingGateNodeRunId);

/// <summary>
///     What a decision recorded, alongside where the run now stands. The decision travels with it so a repeated POST
///     can answer with the same body rather than merely the same run state.
/// </summary>
public sealed record DevWorkflowDecisionResult(DevWorkflowRunDetail Detail, DevWorkflowDecisionSnapshot Decision);

/// <summary>
///     Every way a caller changes a development workflow run.
///     <para>
///         <b>Every method is fire-and-forget.</b> It validates, commits a durable intent, signals the dispatcher and
///         returns the CURRENT state — which may legitimately read <c>Pending</c>, <c>Pausing</c> or <c>Cancelling</c>.
///         Nothing here waits for the runtime to act, which is what keeps the HTTP path off the node's one invocation
///         slot.
///     </para>
///     <para>
///         <b>Every method takes a client-supplied operation id</b>, and a replay of one returns the recorded result
///         without a second effect. That is the same discipline the store enforces one level down, lifted to the verbs
///         a caller actually issues.
///     </para>
///     <para>
///         Three exception types cross this boundary and nothing else, mirroring the work-session surface:
///         <see cref="DevWorkflowNotFoundException" />, <see cref="DevWorkflowInvalidTransitionException" /> for a
///         command the current status forbids (including a second live run on one work item), and
///         <see cref="DevWorkflowValidationException" /> for bad input (including a repo-bound graph on a work item
///         with no project).
///     </para>
/// </summary>
public interface IDevWorkflowRunService
{
    /// <summary>
    ///     Starts a run of <paramref name="definitionId" /> for <paramref name="workItemId" />, pinning the definition's
    ///     graph and creating a node run for every node in it.
    /// </summary>
    /// <param name="inputsJson">
    ///     The caller's seed for this run, carried verbatim into every entry node run's input document and rendered into
    ///     the first agent's objective. There is no run-level column for it: the entry rows ARE where a run's input
    ///     lives.
    /// </param>
    Task<DevWorkflowRunDetail> StartAsync(Guid workItemId, Guid definitionId, string? inputsJson, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> CancelAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> PauseAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> ResumeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The ONE decision surface: a gate's approval and a stuck node run's intervention are the same human act —
    ///     someone unblocking a node run — so they share a table, an endpoint and this method.
    ///     <para>
    ///         A decision the node run's status cannot take is a conflict, not a validation error: the row moved, and
    ///         the answer is to re-read it.
    ///     </para>
    /// </summary>
    /// <param name="decidedBySubject">
    ///     Who decided, carried rather than derived. Without it the audit can say a gate was approved but not by whom.
    /// </param>
    Task<DevWorkflowDecisionResult> DecideAsync(Guid runId,
        Guid nodeRunId,
        Guid operationId,
        DevWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default);
}
