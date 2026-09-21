namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore : IDevelopmentStore
{
    private const string StartupOperationPhase = "StartupInterrupted";

    /// <summary>
    ///     The operation phase of the workspace-secret finding.
    /// </summary>
    /// <remarks>
    ///     A phase of its own, rather than <see cref="DevelopmentOperationPhases.Completed" />, because the idempotency
    ///     key is <c>(project, operation, phase)</c> and the operation id here IS the attempt id — sharing the phase
    ///     with a state transition would make one of them silently return the other's result.
    /// </remarks>
    private const string WorkspaceSecretsOperationPhase = "WorkspaceSecretsDetected";

    /// <summary>
    ///     The operation phase of a workflow's policy injection.
    /// </summary>
    /// <remarks>
    ///     Its own phase for the same reason as the one above: the operation id is the workflow's own deterministic
    ///     one, and sharing a phase with a state transition would make one of them silently return the other's result.
    /// </remarks>
    private const string WorkflowPolicyOperationPhase = "WorkflowPolicyApplied";

    /// <summary>
    ///     camelCase, matching every other document this product puts on a wire.
    /// </summary>
    /// <remarks>
    ///     Read back with the same options so the rows an earlier build wrote in PascalCase still deserialize:
    ///     <c>JsonSerializerDefaults.Web</c> reads case-insensitively, which is what makes re-casing the writes safe on
    ///     an append-only log.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>> LegalTaskTransitions =
        new Dictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>>
        {
            [DevelopmentTaskStatus.Planned] = [DevelopmentTaskStatus.Ready, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Ready] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            // ChangesRequested from InProgress is the workflow lane's other rework ask: it hands an operator's retry sentence to a task whose coder round failed,
            // spending no review round. CALLER-SIDE INVARIANT: DevWorkflowDevTaskExecutor.CarryOperatorRetryAsync first checks the LAST coder attempt did not succeed.
            [DevelopmentTaskStatus.InProgress] =
                [DevelopmentTaskStatus.Validation, DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            // ChangesRequested is the FAILED deterministic gate's target because InProgress could not be: a task the gate rejected sits at InProgress with a
            // SUCCEEDED coder attempt, which StartNextActionAsync re-reads as "validate it" — 289 such runs on one task in 25 minutes. InProgress stays for recovery.
            [DevelopmentTaskStatus.Validation] =
            [
                DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.InReview, DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked,
                DevelopmentTaskStatus.Cancelled
            ],
            [DevelopmentTaskStatus.InReview] = [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.AwaitingApply, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.ChangesRequested] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],

            // ChangesRequested is reachable from AwaitingApply because a workflow's fix loop can route a downstream validation failure back at an implementation
            // node whose task is approved: without it the re-attempt re-succeeds in the same tick and burns the budget. Completed stays absent — that is the apply port's.
            [DevelopmentTaskStatus.AwaitingApply] =
                [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],

            // The one edge out of Blocked, and TransitionTaskAsync refuses it to any command that does not also widen the round cap: not "Blocked is recoverable" but
            // "an operator's Retry can buy the round the cap stopped". Measured: a Retry at N of N re-dispatched the node, which stood itself down ~2 s later, twice.
            [DevelopmentTaskStatus.Blocked] = [DevelopmentTaskStatus.ChangesRequested]
        };

    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public DevelopmentStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }
}
