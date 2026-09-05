namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IDevelopmentStore
{
    private const string StartupOperationPhase = "StartupInterrupted";

    /// <summary>
    ///     The operation phase of the workspace-secret finding. A phase of its own, rather than
    ///     <see cref="DevelopmentOperationPhases.Completed" />, because the idempotency key is
    ///     <c>(project, operation, phase)</c> and the operation id here IS the attempt id — sharing the phase with a
    ///     state transition would make one of them silently return the other's result.
    /// </summary>
    private const string WorkspaceSecretsOperationPhase = "WorkspaceSecretsDetected";

    /// <summary>
    ///     The operation phase of a workflow's policy injection, for the same reason as the one above: the operation id
    ///     is the workflow's own deterministic one, and sharing a phase with a state transition would make one of them
    ///     silently return the other's result.
    /// </summary>
    private const string WorkflowPolicyOperationPhase = "WorkflowPolicyApplied";

    /// <summary>
    ///     camelCase, matching every other document this product puts on a wire, and read back with the same options so
    ///     the rows an earlier build wrote in PascalCase still deserialize — <c>JsonSerializerDefaults.Web</c> reads
    ///     case-insensitively, which is what makes re-casing the writes safe on an append-only log.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>> LegalTaskTransitions =
        new Dictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>>
        {
            [DevelopmentTaskStatus.Planned] = [DevelopmentTaskStatus.Ready, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Ready] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            // ChangesRequested is reachable from InProgress for the workflow lane's other rework ask: an operator who
            // retries a blocked implementation node says WHY, and a task whose coder round failed has no other way to
            // be handed that sentence — the next round would be composed from the same three fields the failed one
            // was. It costs no review round (rounds are spent ENTERING review) and changes no outcome: the next action
            // from either status is a coder round.
            //
            // CALLER-SIDE INVARIANT, and the edge is only safe with it: the one caller,
            // DevWorkflowDevTaskExecutor.CarryOperatorRetryAsync, first checks that the task's LAST coder attempt did
            // not succeed. A task whose coder round DID succeed is on its way to deterministic validation, and asking
            // it for changes here would discard that round's work and its evidence. Any future caller of this edge
            // owes the same check.
            [DevelopmentTaskStatus.InProgress] =
                [DevelopmentTaskStatus.Validation, DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            // ChangesRequested is the FAILED deterministic gate's target, and the edge exists because InProgress could
            // not be one. A task the gate rejected sits at InProgress with a SUCCEEDED coder attempt behind it, which is
            // byte-for-byte the state that means "this round is implemented, validate it" — so StartNextActionAsync read
            // it back and scheduled the same validation again, forever. Measured live: 289 restore/build/test runs on
            // one task in 25 minutes, zero coder rounds, ended only by cancelling the run. ChangesRequested is the one
            // status that says "this implementation was judged wrong" without also saying "and nobody has looked yet",
            // and it is already the reviewer's rejection target, so the next action off it is the coder round the
            // failure is asking for. InProgress stays on the edge for the runner's own recovery hop, which puts a task
            // back where it was when validation produced no usable evidence at all.
            [DevelopmentTaskStatus.Validation] =
            [
                DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.InReview, DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked,
                DevelopmentTaskStatus.Cancelled
            ],
            [DevelopmentTaskStatus.InReview] = [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.AwaitingApply, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.ChangesRequested] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],

            // ChangesRequested is reachable from AwaitingApply because a workflow's fix loop can route a downstream
            // validation failure back at an implementation node whose task is already approved: without this edge the
            // re-attempt has nowhere to go, re-succeeds in the same tick, and the loop burns its budget in seconds
            // without ever asking for a different patch. Completed is deliberately still absent — apply completion is
            // the explicit apply port's, not a generic transition's.
            [DevelopmentTaskStatus.AwaitingApply] =
                [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled]
        };

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
}
