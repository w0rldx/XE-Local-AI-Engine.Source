namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Development-Workflow READ endpoints' only door onto <see cref="IDevWorkflowStore" />: the run list, the
///     paged event log and the artifact feed the run view is drawn from.
/// </summary>
/// <remarks>
///     An endpoint is the HTTP edge and may not take a persistence store itself, so each call arrives here unchanged;
///     this type adds no policy of its own, and the 404-first read, the one-over-the-limit probe, the run-ownership
///     check and the size ceiling stay in the endpoints that own them. Read-only by construction, which is the point
///     of it being separate: commands live on <see cref="IDevWorkflowRunService" />, the composed run detail on
///     <c>DevWorkflowRunComposer</c>, and authoring reads on <see cref="DevWorkflowAuthoringService" />.
/// </remarks>
public sealed class DevWorkflowRunQueryService
{
    private readonly IDevWorkflowStore _store;

    public DevWorkflowRunQueryService(IDevWorkflowStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>The run list, newest first, with each run's definition name and node counters. Both filters optional.</summary>
    public Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return _store.ListRunSummariesAsync(workItemId, status, limit, cancellationToken);
    }

    /// <summary>One run row. The feeds read it first so an unknown run answers 404 rather than an empty page.</summary>
    public Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _store.GetRunAsync(runId, cancellationToken);
    }

    /// <summary>The run's event log from an exclusive watermark. Sequences are strictly increasing but NOT contiguous.</summary>
    public Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _store.ListEventsAsync(runId, sinceSequence, limit, cancellationToken);
    }

    /// <summary>One node run ROW. The caller checks it belongs to the run it was asked for; this member does not.</summary>
    public Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        return _store.GetNodeRunAsync(nodeRunId, cancellationToken);
    }

    /// <summary>The run's decision rows, every node's. The caller filters to the node it is drawing.</summary>
    public Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _store.ListDecisionsAsync(runId, cancellationToken);
    }

    /// <summary>The run's artifacts, every version of every lineage. Append-correct only: a staleness flip never advances the cursor.</summary>
    public Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        return _store.ListArtifactsAsync(runId, sinceSequence, cancellationToken);
    }

    /// <summary>Which artifacts one node run consumed, by id. Ids only: the rows come from the artifact feed above.</summary>
    public Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        return _store.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);
    }

    /// <summary>One artifact ROW — its recorded size, hash and media type. The bytes come from the blob store, not from here.</summary>
    public Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        return _store.GetArtifactAsync(artifactId, cancellationToken);
    }
}
