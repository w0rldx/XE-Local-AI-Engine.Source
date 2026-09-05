namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class DevWorkflowStore
{
    public async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken).ConfigureAwait(false);

        // No sinceSequence filter: a node-run's sequence is its insert order, so filtering on it would hide every
        // status change the caller actually came for.
        var nodeRuns = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderBy(entity => entity.Sequence)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        var available = await LoadAvailableWorkSessionsAsync([.. nodeRuns.Where(entity => entity.WorkSessionId is not null).Select(entity => entity.WorkSessionId!.Value)],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. nodeRuns.Select(entity => NodeRunSnapshot(entity, available))];
    }

    public async Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                        .Where(entity => entity.WorkSessionId != null)
                        .Select(entity => entity.WorkSessionId!.Value)
                        .Distinct()
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

    /// <summary>
    ///     Latest wins, and latest is the node run created last; the id breaks a tie inside one materialization's
    ///     insert rather than leaving the answer to whatever order the database happens to return.
    /// </summary>
    public async Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default) =>
        await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                        .Where(entity => entity.DevelopmentTaskId == developmentTaskId)
                        .OrderByDescending(entity => entity.CreatedAtUtc)
                        .ThenByDescending(entity => entity.Id)
                        .Select(entity => (Guid?)entity.RunId)
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

    /// <summary>
    ///     The batch form, and the same "latest wins" rule: one query returns every (task, run) pointer for the ids
    ///     asked about, and the pick is made over the ordered rows in memory — the projection is two columns per node
    ///     run, so what comes back is small even for a project with a long attempt history.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(developmentTaskIds);
        if (developmentTaskIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var rows = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                   .Where(entity => entity.DevelopmentTaskId != null && developmentTaskIds.Contains(entity.DevelopmentTaskId.Value))
                                   .OrderByDescending(entity => entity.CreatedAtUtc)
                                   .ThenByDescending(entity => entity.Id)
                                   .Select(entity => new DevelopmentTaskRunRow(entity.DevelopmentTaskId!.Value, entity.RunId))
                                   .ToListAsync(cancellationToken)
                                   .ConfigureAwait(false);

        return rows.GroupBy(static row => row.DevelopmentTaskId)
                   .ToDictionary(static group => group.Key, static group => group.First().RunId);
    }

    public async Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        var nodeRun = await _dbContext.DevWorkflowNodeRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == nodeRunId, cancellationToken).ConfigureAwait(false)
                      ?? throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found.");
        var available = await LoadAvailableWorkSessionsAsync(nodeRun.WorkSessionId is { } sessionId ? [sessionId] : [], cancellationToken).ConfigureAwait(false);
        return NodeRunSnapshot(nodeRun, available);
    }

    public async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken).ConfigureAwait(false);

        // The artifact cursor is append-correct only. The sequence is allocated at insert and never re-stamped, so a
        // sinceSequence page returns every artifact that has APPEARED since — and no staleness flip that has happened
        // since. Staleness mutations are announced on the event feed as artifact.stale.marked and read by refetching
        // the artifact, never by advancing this cursor. IsLatest is derived from the whole lineage, which is why that
        // filter cannot be pushed into SQL.
        var artifacts = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                        .Where(entity => entity.RunId == runId)
                                        .OrderBy(entity => entity.Sequence)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        var latest = LatestVersionPerLineage(artifacts);
        return [.. artifacts.Where(entity => entity.Sequence > sinceSequence).Select(entity => ArtifactSnapshot(entity, latest))];
    }

    public async Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.DevWorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new DevWorkflowNotFoundException($"Development workflow artifact '{artifactId}' was not found.");
        var highest = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                      .Where(entity => entity.LineageId == artifact.LineageId)
                                      .MaxAsync(entity => entity.Version, cancellationToken)
                                      .ConfigureAwait(false);
        return ArtifactSnapshot(artifact, new Dictionary<Guid, int>
        {
            [artifact.LineageId] = highest
        });
    }

    public async Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        // Served by ux_dev_workflow_artifact_uses_node_artifact; it exists here rather than as a direct query from the
        // API layer so nothing outside this store reaches into the tables.
        return await _dbContext.DevWorkflowArtifactUses.AsNoTracking()
                               .Where(entity => entity.NodeRunId == nodeRunId)
                               .OrderBy(entity => entity.RecordedSequence)
                               .Select(entity => entity.ArtifactId)
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken).ConfigureAwait(false);

        var decisions = await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                        .Where(entity => entity.RunId == runId)
                                        .OrderBy(entity => entity.Sequence)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return [.. decisions.Select(DecisionSnapshot)];
    }

    public async Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default)
    {
        var decision = await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.OperationId == operationId, cancellationToken)
                                       .ConfigureAwait(false);
        return decision is null ? null : DecisionSnapshot(decision);
    }

    public async Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "An event page limit must be positive.");
        }

        await EnsureRunExistsAsync(runId, cancellationToken).ConfigureAwait(false);

        // Events are the append-only feed — never re-stamped — so their watermark is also their order.
        var events = await _dbContext.DevWorkflowRunEvents.AsNoTracking()
                                     .Where(entity => entity.RunId == runId && entity.Sequence > sinceSequence)
                                     .OrderBy(entity => entity.Sequence)
                                     .Take(limit)
                                     .ToListAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return
        [
            .. events.Select(entity => new DevWorkflowRunEventSnapshot(entity.Id,
                entity.RunId,
                entity.NodeRunId,
                entity.Sequence,
                entity.EventType,
                TextOrNull(entity.DetailJson),
                entity.OperationId,
                entity.Outcome,
                entity.OccurredAtUtc))
        ];
    }

    private async Task EnsureRunExistsAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.DevWorkflowRuns.AsNoTracking().AnyAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false))
        {
            throw new DevWorkflowNotFoundException($"Development workflow run '{runId}' was not found.");
        }
    }

    /// <summary>
    ///     Which of these work sessions still exist. A purged conversation takes its session's whole subtree with it, so
    ///     a node-run's pointer can outlive its target — and that has to read back as "transcript no longer available"
    ///     rather than as an error.
    /// </summary>
    private async Task<HashSet<Guid>> LoadAvailableWorkSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var found = await _dbContext.AgentWorkSessions.AsNoTracking()
                                    .Where(entity => sessionIds.Contains(entity.Id))
                                    .Select(entity => entity.Id)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        return [.. found];
    }

    /// <summary>The highest version per lineage — what <c>IsLatest</c> is, computed rather than stored so two writes can never disagree.</summary>
    private static Dictionary<Guid, int> LatestVersionPerLineage(IEnumerable<DevWorkflowArtifact> artifacts) =>
        artifacts.GroupBy(entity => entity.LineageId).ToDictionary(group => group.Key, group => group.Max(entity => entity.Version));

    private static DevWorkflowNodeRunSnapshot NodeRunSnapshot(DevWorkflowNodeRun nodeRun, IReadOnlySet<Guid> availableWorkSessions) =>
        new(nodeRun.Id,
            nodeRun.RunId,
            nodeRun.NodeKey,
            nodeRun.NodeType,
            nodeRun.Attempt,
            nodeRun.MaxAttempts,
            nodeRun.SessionResumes,
            nodeRun.Status,
            nodeRun.QueueReason,
            nodeRun.PendingDecisionKind,
            nodeRun.Sequence,
            nodeRun.WorkSessionId,
            nodeRun.WorkSessionId is { } sessionId && availableWorkSessions.Contains(sessionId),
            nodeRun.AgentDefinitionId,
            nodeRun.DevelopmentProjectId,
            nodeRun.DevelopmentTaskId,
            TextOrNull(nodeRun.InputJson),
            TextOrNull(nodeRun.OutputJson),
            TextOrNull(nodeRun.PolicyResolutionJson),
            nodeRun.MaterializedFromNodeRunId,
            nodeRun.MaterializationIndex,
            nodeRun.FailureClass,
            nodeRun.TerminalReason,
            nodeRun.QueuedAtUtc,
            nodeRun.StartedAtUtc,
            nodeRun.EndedAtUtc,
            nodeRun.CreatedAtUtc,
            nodeRun.InputTokens,
            nodeRun.OutputTokens,
            nodeRun.ReasoningTokens,
            nodeRun.EstimatedInputTokens,
            nodeRun.ProviderCalls,
            nodeRun.ToolCalls,
            nodeRun.ToolSchemaTokens,
            nodeRun.ToolNamesJson,
            nodeRun.AgentTurnMs,
            nodeRun.ServedModelName,
            nodeRun.RouteJson,
            nodeRun.WorkSessionSteps,
            nodeRun.ModelReadinessMs,
            nodeRun.VramFreeAtLoadBytes,
            nodeRun.VramAdmittedBytes);

    private static DevWorkflowArtifactSnapshot ArtifactSnapshot(DevWorkflowArtifact artifact, IReadOnlyDictionary<Guid, int> latestVersions) =>
        new(artifact.Id,
            artifact.RunId,
            artifact.LineageId,
            artifact.ProducingNodeKey,
            artifact.ProducedByNodeRunId,
            artifact.Name,
            artifact.Version,
            latestVersions.TryGetValue(artifact.LineageId, out var latest) && latest == artifact.Version,
            artifact.Kind,
            artifact.MediaType,
            artifact.ContentSha256,
            artifact.SizeBytes,
            artifact.IsValid,
            artifact.IsStale,
            artifact.StaleSinceSequence,
            artifact.StaleBecauseArtifactId,
            artifact.StaleReason,
            artifact.ManagedReference,
            artifact.Sequence,
            artifact.CreatedAtUtc);

    private static DevWorkflowDecisionSnapshot DecisionSnapshot(DevWorkflowDecision decision) =>
        new(decision.Id,
            decision.RunId,
            decision.NodeRunId,
            decision.Attempt,
            decision.Decision,
            TextOrNull(decision.Comment),
            TextOrNull(decision.PayloadJson),
            decision.DecidedBySubject,
            decision.OperationId,
            decision.Sequence,
            decision.DecidedAtUtc);

    private sealed record DevelopmentTaskRunRow(Guid DevelopmentTaskId, Guid RunId);
}
