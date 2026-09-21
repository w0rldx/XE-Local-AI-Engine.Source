namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class DevWorkflowStore
{
    public async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken);

        // No sinceSequence filter: a node-run's sequence is its insert order, so filtering on it would hide every
        // status change the caller actually came for.
        var nodeRuns = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderBy(entity => entity.Sequence)
                                       .ToListAsync(cancellationToken);
        var available = await LoadAvailableWorkSessionsAsync([.. nodeRuns.Where(entity => entity.WorkSessionId is not null).Select(entity => entity.WorkSessionId!.Value)],
                cancellationToken);
        return [.. nodeRuns.Select(entity => NodeRunSnapshot(entity, available))];
    }

    public async Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                        .Where(entity => entity.WorkSessionId != null)
                        .Select(entity => entity.WorkSessionId!.Value)
                        .Distinct()
                        .ToListAsync(cancellationToken);

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
                        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    ///     The batch form, and the same "latest wins" rule: one query returns every (task, run) pointer for the ids
    ///     asked about, and the pick is made over the ordered rows in memory.
    /// </summary>
    /// <remarks>
    ///     The projection is two columns per node run, so what comes back is small even for a project with a long
    ///     attempt history.
    /// </remarks>
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
                                   .Select(entity => new DevelopmentTaskRunRow { DevelopmentTaskId = entity.DevelopmentTaskId!.Value, RunId = entity.RunId })
                                   .ToListAsync(cancellationToken);

        return rows.GroupBy(static row => row.DevelopmentTaskId)
                   .ToDictionary(static group => group.Key, static group => group.First().RunId);
    }

    public async Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        var nodeRun = await _dbContext.DevWorkflowNodeRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == nodeRunId, cancellationToken)
                      ?? throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found.");
        var available = await LoadAvailableWorkSessionsAsync(nodeRun.WorkSessionId is { } sessionId ? [sessionId] : [], cancellationToken);
        return NodeRunSnapshot(nodeRun, available);
    }

    public async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken);

        // The artifact cursor is append-correct only: the sequence is allocated at insert and never re-stamped, so a sinceSequence page returns every artifact that
        // APPEARED since and no staleness flip — those come on the event feed as artifact.stale.marked. IsLatest is derived from the lineage, so it cannot go in SQL.
        var artifacts = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                        .Where(entity => entity.RunId == runId)
                                        .OrderBy(entity => entity.Sequence)
                                        .ToListAsync(cancellationToken);
        var latest = LatestVersionPerLineage(artifacts);
        return [.. artifacts.Where(entity => entity.Sequence > sinceSequence).Select(entity => ArtifactSnapshot(entity, latest))];
    }

    public async Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.DevWorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                       ?? throw new DevWorkflowNotFoundException($"Development workflow artifact '{artifactId}' was not found.");
        var highest = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                      .Where(entity => entity.LineageId == artifact.LineageId)
                                      .MaxAsync(entity => entity.Version, cancellationToken);
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
                               .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureRunExistsAsync(runId, cancellationToken);

        var decisions = await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                        .Where(entity => entity.RunId == runId)
                                        .OrderBy(entity => entity.Sequence)
                                        .ToListAsync(cancellationToken);
        return [.. decisions.Select(DecisionSnapshot)];
    }

    public async Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default)
    {
        var decision = await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.OperationId == operationId, cancellationToken);
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

        await EnsureRunExistsAsync(runId, cancellationToken);

        // Events are the append-only feed — never re-stamped — so their watermark is also their order.
        var events = await _dbContext.DevWorkflowRunEvents.AsNoTracking()
                                     .Where(entity => entity.RunId == runId && entity.Sequence > sinceSequence)
                                     .OrderBy(entity => entity.Sequence)
                                     .Take(limit)
                                     .ToListAsync(cancellationToken);
        return
        [
            .. events.Select(entity => new DevWorkflowRunEventSnapshot
            {
                Id = entity.Id,
                RunId = entity.RunId,
                NodeRunId = entity.NodeRunId,
                Sequence = entity.Sequence,
                EventType = entity.EventType,
                DetailJson = TextOrNull(entity.DetailJson),
                OperationId = entity.OperationId,
                Outcome = entity.Outcome,
                OccurredAtUtc = entity.OccurredAtUtc
            })
        ];
    }

    private async Task EnsureRunExistsAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.DevWorkflowRuns.AsNoTracking().AnyAsync(entity => entity.Id == runId, cancellationToken))
        {
            throw new DevWorkflowNotFoundException($"Development workflow run '{runId}' was not found.");
        }
    }

    /// <summary>
    ///     Which of these work sessions still exist.
    /// </summary>
    /// <remarks>
    ///     A purged conversation takes its session's whole subtree with it, so a node-run's pointer can outlive its
    ///     target — and that has to read back as "transcript no longer available" rather than as an error.
    /// </remarks>
    private async Task<HashSet<Guid>> LoadAvailableWorkSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var found = await _dbContext.AgentWorkSessions.AsNoTracking()
                                    .Where(entity => sessionIds.Contains(entity.Id))
                                    .Select(entity => entity.Id)
                                    .ToListAsync(cancellationToken);
        return [.. found];
    }

    /// <summary>The highest version per lineage — what <c>IsLatest</c> is, computed rather than stored so two writes can never disagree.</summary>
    private static Dictionary<Guid, int> LatestVersionPerLineage(IEnumerable<DevWorkflowArtifact> artifacts) =>
        artifacts.GroupBy(entity => entity.LineageId).ToDictionary(group => group.Key, group => group.Max(entity => entity.Version));

    private static DevWorkflowNodeRunSnapshot NodeRunSnapshot(DevWorkflowNodeRun nodeRun, IReadOnlySet<Guid> availableWorkSessions) =>
        new()
        {
            Id = nodeRun.Id,
            RunId = nodeRun.RunId,
            NodeKey = nodeRun.NodeKey,
            NodeType = nodeRun.NodeType,
            Attempt = nodeRun.Attempt,
            MaxAttempts = nodeRun.MaxAttempts,
            SessionResumes = nodeRun.SessionResumes,
            Status = nodeRun.Status,
            QueueReason = nodeRun.QueueReason,
            PendingDecisionKind = nodeRun.PendingDecisionKind,
            Sequence = nodeRun.Sequence,
            WorkSessionId = nodeRun.WorkSessionId,
            WorkSessionAvailable = nodeRun.WorkSessionId is { } sessionId && availableWorkSessions.Contains(sessionId),
            AgentDefinitionId = nodeRun.AgentDefinitionId,
            DevelopmentProjectId = nodeRun.DevelopmentProjectId,
            DevelopmentTaskId = nodeRun.DevelopmentTaskId,
            InputJson = TextOrNull(nodeRun.InputJson),
            OutputJson = TextOrNull(nodeRun.OutputJson),
            PolicyResolutionJson = TextOrNull(nodeRun.PolicyResolutionJson),
            MaterializedFromNodeRunId = nodeRun.MaterializedFromNodeRunId,
            MaterializationIndex = nodeRun.MaterializationIndex,
            FailureClass = nodeRun.FailureClass,
            TerminalReason = nodeRun.TerminalReason,
            QueuedAtUtc = nodeRun.QueuedAtUtc,
            StartedAtUtc = nodeRun.StartedAtUtc,
            EndedAtUtc = nodeRun.EndedAtUtc,
            CreatedAtUtc = nodeRun.CreatedAtUtc,
            InputTokens = nodeRun.InputTokens,
            OutputTokens = nodeRun.OutputTokens,
            ReasoningTokens = nodeRun.ReasoningTokens,
            EstimatedInputTokens = nodeRun.EstimatedInputTokens,
            ProviderCalls = nodeRun.ProviderCalls,
            ToolCalls = nodeRun.ToolCalls,
            ToolSchemaTokens = nodeRun.ToolSchemaTokens,
            ToolNamesJson = nodeRun.ToolNamesJson,
            AgentTurnMs = nodeRun.AgentTurnMs,
            ServedModelName = nodeRun.ServedModelName,
            RouteJson = nodeRun.RouteJson,
            WorkSessionSteps = nodeRun.WorkSessionSteps,
            ModelReadinessMs = nodeRun.ModelReadinessMs,
            VramFreeAtLoadBytes = nodeRun.VramFreeAtLoadBytes,
            VramAdmittedBytes = nodeRun.VramAdmittedBytes
        };

    private static DevWorkflowArtifactSnapshot ArtifactSnapshot(DevWorkflowArtifact artifact, IReadOnlyDictionary<Guid, int> latestVersions) =>
        new()
        {
            Id = artifact.Id,
            RunId = artifact.RunId,
            LineageId = artifact.LineageId,
            ProducingNodeKey = artifact.ProducingNodeKey,
            ProducedByNodeRunId = artifact.ProducedByNodeRunId,
            Name = artifact.Name,
            Version = artifact.Version,
            IsLatest = latestVersions.TryGetValue(artifact.LineageId, out var latest) && latest == artifact.Version,
            Kind = artifact.Kind,
            MediaType = artifact.MediaType,
            ContentSha256 = artifact.ContentSha256,
            SizeBytes = artifact.SizeBytes,
            IsValid = artifact.IsValid,
            IsStale = artifact.IsStale,
            StaleSinceSequence = artifact.StaleSinceSequence,
            StaleBecauseArtifactId = artifact.StaleBecauseArtifactId,
            StaleReason = artifact.StaleReason,
            ManagedReference = artifact.ManagedReference,
            Sequence = artifact.Sequence,
            CreatedAtUtc = artifact.CreatedAtUtc
        };

    private static DevWorkflowDecisionSnapshot DecisionSnapshot(DevWorkflowDecision decision) =>
        new()
        {
            Id = decision.Id,
            RunId = decision.RunId,
            NodeRunId = decision.NodeRunId,
            Attempt = decision.Attempt,
            Decision = decision.Decision,
            Comment = TextOrNull(decision.Comment),
            PayloadJson = TextOrNull(decision.PayloadJson),
            DecidedBySubject = decision.DecidedBySubject,
            OperationId = decision.OperationId,
            Sequence = decision.Sequence,
            DecidedAtUtc = decision.DecidedAtUtc
        };

    private sealed record DevelopmentTaskRunRow
    {
        public required Guid DevelopmentTaskId { get; init; }

        public required Guid RunId { get; init; }
    }
}
