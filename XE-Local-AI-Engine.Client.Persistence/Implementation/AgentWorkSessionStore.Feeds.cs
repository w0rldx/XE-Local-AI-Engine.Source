namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class AgentWorkSessionStore
{
    public async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ListTasksAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // sinceSequence filters and never orders: a task re-stamped by an update would otherwise jump to the end of a
        // sequence-ordered page every time the agent touched it.
        var tasks = await _dbContext.AgentWorkSessionTasks.AsNoTracking()
                                    .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                    .OrderBy(entity => entity.CreatedStep)
                                    .ThenBy(entity => entity.Id)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        return
        [
            .. tasks.Select(entity => new WorkSessionTaskSnapshot(entity.Id,
                entity.SessionId,
                entity.ParentTaskId,
                entity.Sequence,
                Text(entity.Title),
                TextOrNull(entity.Detail),
                entity.Status,
                TextOrNull(entity.BlockedReason),
                entity.Origin,
                entity.CreatedStep,
                entity.UpdatedStep))
        ];
    }

    public async Task<IReadOnlyList<WorkSessionFindingSnapshot>> ListFindingsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var findings = await _dbContext.AgentWorkSessionFindings.AsNoTracking()
                                       .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                       .OrderBy(entity => entity.CreatedStep)
                                       .ThenBy(entity => entity.Id)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return
        [
            .. findings.Select(entity => new WorkSessionFindingSnapshot(entity.Id,
                entity.SessionId,
                entity.TaskId,
                entity.Sequence,
                entity.Kind,
                Text(entity.Text),
                TextOrNull(entity.SourceRef),
                entity.CreatedStep,
                entity.Superseded))
        ];
    }

    public async Task<IReadOnlyList<WorkSessionArtifactSnapshot>> ListArtifactsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var artifacts = await _dbContext.AgentWorkSessionArtifacts.AsNoTracking()
                                        .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                        .OrderBy(entity => entity.CreatedStep)
                                        .ThenBy(entity => entity.Id)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return [.. artifacts.Select(ArtifactSnapshot)];
    }

    public async Task<IReadOnlyList<WorkSessionCheckpointSnapshot>> ListCheckpointsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var checkpoints = await _dbContext.AgentWorkSessionCheckpoints.AsNoTracking()
                                          .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                          .OrderBy(entity => entity.Step)
                                          .ThenBy(entity => entity.Id)
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);
        return [.. checkpoints.Select(CheckpointSnapshot)];
    }

    public async Task<IReadOnlyList<WorkSessionEventSnapshot>> ListEventsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // Events are the one append-only feed — never re-stamped — so their watermark is also their order.
        var events = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                     .Where(entity => entity.SessionId == sessionId && entity.Sequence > sinceSequence)
                                     .OrderBy(entity => entity.Sequence)
                                     .ToListAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return
        [
            .. events.Select(entity => new WorkSessionEventSnapshot(entity.Id,
                entity.SessionId,
                entity.Sequence,
                entity.Step,
                entity.EventType,
                TextOrNull(entity.DetailJson),
                entity.OperationId,
                entity.Outcome,
                entity.OccurredAtUtc))
        ];
    }

    public async Task<WorkSessionEventSnapshot?> FindLatestEventAsync(Guid sessionId, string eventType, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // The event type is a plain column — only the detail is encrypted — so the filter and the order both run in
        // SQL, and exactly one row comes back to decrypt.
        var latest = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                     .Where(entity => entity.SessionId == sessionId && entity.EventType == eventType)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return latest is null
            ? null
            : new WorkSessionEventSnapshot(latest.Id,
                latest.SessionId,
                latest.Sequence,
                latest.Step,
                latest.EventType,
                TextOrNull(latest.DetailJson),
                latest.OperationId,
                latest.Outcome,
                latest.OccurredAtUtc);
    }

    public async Task<WorkSessionArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.AgentWorkSessionArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                                       .ConfigureAwait(false)
                       ?? throw new WorkSessionNotFoundException($"Work session artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }

    public async Task<WorkSessionCheckpointSnapshot?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var checkpoint = await _dbContext.AgentWorkSessionCheckpoints.AsNoTracking()
                                         .Where(entity => entity.SessionId == sessionId)
                                         .OrderByDescending(entity => entity.Sequence)
                                         .FirstOrDefaultAsync(cancellationToken)
                                         .ConfigureAwait(false);
        return checkpoint is null ? null : CheckpointSnapshot(checkpoint);
    }

    private async Task EnsureSessionExistsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.AgentWorkSessions.AsNoTracking()
                             .AnyAsync(entity => entity.Id == sessionId, cancellationToken)
                             .ConfigureAwait(false))
        {
            throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
        }
    }
}
