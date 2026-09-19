namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class AgentWorkSessionStore
{
    public async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ListTasksAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        // sinceSequence filters and never orders: a task re-stamped by an update would otherwise jump to the end of a
        // sequence-ordered page every time the agent touched it.
        var tasks = await _dbContext.AgentWorkSessionTasks.AsNoTracking()
                                    .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                    .OrderBy(entity => entity.CreatedStep)
                                    .ThenBy(entity => entity.Id)
                                    .ToListAsync(cancellationToken);
        return
        [
            .. tasks.Select(entity => new WorkSessionTaskSnapshot
            {
                Id = entity.Id,
                SessionId = entity.SessionId,
                ParentTaskId = entity.ParentTaskId,
                Sequence = entity.Sequence,
                Title = Text(entity.Title),
                Detail = TextOrNull(entity.Detail),
                Status = entity.Status,
                BlockedReason = TextOrNull(entity.BlockedReason),
                Origin = entity.Origin,
                CreatedStep = entity.CreatedStep,
                UpdatedStep = entity.UpdatedStep
            })
        ];
    }

    public async Task<IReadOnlyList<WorkSessionFindingSnapshot>> ListFindingsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        var findings = await _dbContext.AgentWorkSessionFindings.AsNoTracking()
                                       .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                       .OrderBy(entity => entity.CreatedStep)
                                       .ThenBy(entity => entity.Id)
                                       .ToListAsync(cancellationToken);
        return
        [
            .. findings.Select(entity => new WorkSessionFindingSnapshot
            {
                Id = entity.Id,
                SessionId = entity.SessionId,
                TaskId = entity.TaskId,
                Sequence = entity.Sequence,
                Kind = entity.Kind,
                Text = Text(entity.Text),
                SourceRef = TextOrNull(entity.SourceRef),
                CreatedStep = entity.CreatedStep,
                Superseded = entity.Superseded
            })
        ];
    }

    public async Task<IReadOnlyList<WorkSessionArtifactSnapshot>> ListArtifactsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        var artifacts = await _dbContext.AgentWorkSessionArtifacts.AsNoTracking()
                                        .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                        .OrderBy(entity => entity.CreatedStep)
                                        .ThenBy(entity => entity.Id)
                                        .ToListAsync(cancellationToken);
        return [.. artifacts.Select(ArtifactSnapshot)];
    }

    public async Task<IReadOnlyList<WorkSessionCheckpointSnapshot>> ListCheckpointsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        var checkpoints = await _dbContext.AgentWorkSessionCheckpoints.AsNoTracking()
                                          .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                          .OrderBy(entity => entity.Step)
                                          .ThenBy(entity => entity.Id)
                                          .ToListAsync(cancellationToken);
        return [.. checkpoints.Select(CheckpointSnapshot)];
    }

    public async Task<IReadOnlyList<WorkSessionEventSnapshot>> ListEventsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        // Events are the one append-only feed — never re-stamped — so their watermark is also their order.
        var events = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                     .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                     .OrderBy(entity => entity.Sequence)
                                     .ToListAsync(cancellationToken);
        return
        [
            .. events.Select(entity => new WorkSessionEventSnapshot
            {
                Id = entity.Id,
                SessionId = entity.SessionId,
                Sequence = entity.Sequence,
                Step = entity.Step,
                EventType = entity.EventType,
                DetailJson = TextOrNull(entity.DetailJson),
                OperationId = entity.OperationId,
                Outcome = entity.Outcome,
                OccurredAtUtc = entity.OccurredAtUtc
            })
        ];
    }

    public async Task<WorkSessionEventSnapshot?> FindLatestEventAsync(Guid sessionId, string eventType, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        // The event type is a plain column — only the detail is encrypted — so the filter and the order both run in
        // SQL, and exactly one row comes back to decrypt.
        var latest = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                     .Where(entity => entity.SessionId == sessionId && entity.EventType == eventType)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken);
        return latest is null
            ? null
            : new WorkSessionEventSnapshot
            {
                Id = latest.Id,
                SessionId = latest.SessionId,
                Sequence = latest.Sequence,
                Step = latest.Step,
                EventType = latest.EventType,
                DetailJson = TextOrNull(latest.DetailJson),
                OperationId = latest.OperationId,
                Outcome = latest.Outcome,
                OccurredAtUtc = latest.OccurredAtUtc
            };
    }

    public async Task<WorkSessionArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.AgentWorkSessionArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                       ?? throw new WorkSessionNotFoundException($"Work session artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }

    public async Task<WorkSessionCheckpointSnapshot?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);

        var checkpoint = await _dbContext.AgentWorkSessionCheckpoints.AsNoTracking()
                                         .Where(entity => entity.SessionId == sessionId)
                                         .OrderByDescending(entity => entity.Sequence)
                                         .FirstOrDefaultAsync(cancellationToken);
        return checkpoint is null ? null : CheckpointSnapshot(checkpoint);
    }

    private async Task EnsureSessionExistsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.AgentWorkSessions.AsNoTracking()
                             .AnyAsync(entity => entity.Id == sessionId, cancellationToken))
        {
            throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
        }
    }
}
