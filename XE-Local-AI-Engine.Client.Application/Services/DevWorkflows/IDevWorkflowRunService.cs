namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A run and its node runs in one read, composed from the store's snapshots rather than re-declaring their
///     fields.
/// </summary>
/// <remarks>
///     The GRAPH is deliberately not in it: a caller that needs nodes and edges decrypts the run's pinned blob,
///     which the runtime already holds parsed and has no reason to re-serialize.
/// </remarks>
public sealed class DevWorkflowRunDetail
{
    public required DevWorkflowRunSnapshot Run { get; init; }

    public required IReadOnlyList<DevWorkflowNodeRunSnapshot> NodeRuns { get; init; }

    /// <summary>Node runs waiting on a human — a gate's approval and an exhausted node's intervention alike.</summary>
    public required int PendingDecisionCount { get; init; }

    /// <summary>The oldest node run waiting on a gate answer, in sequence order, if there is one.</summary>
    public required Guid? BlockingGateNodeRunId { get; init; }
}

/// <summary>
///     What a decision recorded, alongside where the run now stands. The decision travels with it so a repeated POST
///     can answer with the same body rather than merely the same run state.
/// </summary>
public sealed class DevWorkflowDecisionResult
{
    public required DevWorkflowRunDetail Detail { get; init; }

    public required DevWorkflowDecisionSnapshot Decision { get; init; }
}

/// <summary>Every way a caller changes a development workflow run.</summary>
/// <remarks>
///     Every method is FIRE-AND-FORGET: it validates, commits a durable intent, signals the dispatcher and returns
///     the CURRENT state, which may legitimately read <c>Pending</c>, <c>Pausing</c> or <c>Cancelling</c> — nothing
///     waits for the runtime, which keeps the HTTP path off the node's one invocation slot. Every method takes a
///     client-supplied operation id, and a replay returns the recorded result without a second effect. Which
///     failures cross this boundary, and how they map: docs/wiki/25-dev-workflows.md ("Human decisions").
/// </remarks>
public interface IDevWorkflowRunService
{
    /// <summary>
    ///     Starts a run of <paramref name="definitionId" /> for <paramref name="workItemId" />, pinning the definition's
    ///     graph and creating a node run for every node in it.
    /// </summary>
    /// <param name="inputsJson">
    ///     The caller's seed, carried verbatim into every entry node run's input document. There is no run-level
    ///     column: the entry rows ARE where a run's input lives.
    /// </param>
    Task<DevWorkflowRunDetail> StartAsync(Guid workItemId, Guid definitionId, string? inputsJson, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> CancelAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> PauseAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> ResumeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunDetail> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes a work item and everything under it, refusing while one of its runs is still live.
    /// </summary>
    /// <remarks>
    ///     It lives on the RUN service because the rows are the smaller half of the job: the delete also releases the
    ///     work sessions the agent node runs own and the artifact bytes on disk, neither of which the store reaches.
    ///     Ordered so a refusal costs nothing — the live-run check first, then sessions, then rows, then bytes.
    /// </remarks>
    Task DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The ONE decision surface: a gate's approval and a stuck node run's intervention are the same human act —
    ///     someone unblocking a node run — so they share a table, an endpoint and this method.
    /// </summary>
    /// <remarks>
    ///     A decision the node run's status cannot take is a conflict, not a validation error: the row moved, and the
    ///     answer is to re-read it.
    /// </remarks>
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
