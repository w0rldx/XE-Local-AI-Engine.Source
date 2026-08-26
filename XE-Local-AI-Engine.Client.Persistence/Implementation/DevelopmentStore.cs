namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    private static readonly IReadOnlyDictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>> LegalTaskTransitions =
        new Dictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>>
        {
            [DevelopmentTaskStatus.Planned] = [DevelopmentTaskStatus.Ready, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Ready] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.InProgress] = [DevelopmentTaskStatus.Validation, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Validation] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.InReview, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.InReview] = [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.AwaitingApply, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.ChangesRequested] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.AwaitingApply] = [DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled]
        };

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

}
