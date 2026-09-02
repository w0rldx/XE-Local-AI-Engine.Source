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
            [DevelopmentTaskStatus.InProgress] = [DevelopmentTaskStatus.Validation, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Validation] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.InReview, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
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
