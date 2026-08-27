namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class AgentWorkSessionStore
{
    public async Task<AgentWorkSessionSnapshot> CreateAsync(CreateWorkSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Title, nameof(command.Title));
        EnsureNotBlank(command.Objective, nameof(command.Objective));
        if (command.Kind == AgentWorkSessionKind.Development)
        {
            // Reserved until the Development kind has an execution path. Persisting one now would create rows that
            // nothing can run and that no later migration could tell apart from a supported session.
            throw new ArgumentException("Development work sessions are not supported yet.", nameof(command));
        }

        if (command.ConfigVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "The configuration version must be positive.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _dbContext.AgentWorkSessions.AnyAsync(entity => entity.Id == command.SessionId || entity.ConversationId == command.ConversationId, cancellationToken)
                                .ConfigureAwait(false))
            {
                throw new WorkSessionConcurrencyException($"A work session already exists for id '{command.SessionId}' or its conversation.");
            }

            var now = Now();
            var session = new AgentWorkSession
            {
                Id = command.SessionId,
                Title = command.Title,
                Objective = Utf8(command.Objective),
                Kind = command.Kind,
                AgentDefinitionId = command.AgentDefinitionId,
                ConversationId = command.ConversationId,
                Status = AgentWorkSessionStatus.Draft,
                StepCount = 0,
                LastSequence = 0,
                ConfigVersion = command.ConfigVersion,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1
            };
            _dbContext.AgentWorkSessions.Add(session);
            AddEvent(session, "SessionCreated", session.Status.ToString(), operationId: null, detailJson: null);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Snapshot(session);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new WorkSessionConcurrencyException("A concurrent writer won the race before the work session was created.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AgentWorkSessionSnapshot> UpdateAsync(UpdateWorkSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Title is not null)
        {
            EnsureNotBlank(command.Title, nameof(command.Title));
        }

        if (command.Objective is not null)
        {
            EnsureNotBlank(command.Objective, nameof(command.Objective));
        }

        AgentWorkSession? updated = null;
        _ = await ExecuteMutationAsync(command.SessionId,
                command.ExpectedVersion,
                operationId: null,
                session =>
                {
                    if (command.Title is not null)
                    {
                        session.Title = command.Title;
                    }

                    if (command.Objective is not null)
                    {
                        session.Objective = Utf8(command.Objective);
                    }

                    if (command.AgentDefinitionId is { } agentDefinitionId)
                    {
                        session.AgentDefinitionId = agentDefinitionId;
                    }

                    updated = session;
                    return Task.FromResult(new MutationOutcome("SessionUpdated", session.Status.ToString(), DetailJson: null));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return Snapshot(updated!);
    }

    public async Task<AgentWorkSessionSnapshot> TransitionStatusAsync(TransitionWorkSessionStatusCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentWorkSession? updated = null;
        _ = await ExecuteMutationAsync(command.SessionId,
                command.ExpectedVersion,
                operationId: null,
                session =>
                {
                    if (command.TargetStatus == AgentWorkSessionStatus.Interrupted)
                    {
                        // Only the startup reconcile writes Interrupted: it is the record of a host that died, which no
                        // live caller is in a position to assert.
                        throw new WorkSessionInvalidTransitionException("Interrupted is written only by the startup reconciliation.");
                    }

                    ApplyStatus(session, command.TargetStatus, command.CurrentTaskId);
                    updated = session;
                    return Task.FromResult(new MutationOutcome("SessionStatusChanged", command.TargetStatus.ToString(), ReasonDetail(command.SanitizedReason)));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return Snapshot(updated!);
    }

    public async Task<int> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // Explicit ordered deletes: the node connection runs without PRAGMA foreign_keys, so the declared cascades are
        // documentation only and an EF-graph delete would leave every child table populated.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = await _dbContext.AgentWorkSessionEvents.Where(entity => entity.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await _dbContext.AgentWorkSessionCheckpoints.Where(entity => entity.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await _dbContext.AgentWorkSessionArtifacts.Where(entity => entity.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await _dbContext.AgentWorkSessionFindings.Where(entity => entity.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await _dbContext.AgentWorkSessionTasks.Where(entity => entity.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await _dbContext.AgentWorkSessions.Where(entity => entity.Id == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            return removed;
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new WorkSessionConcurrencyException("The work session could not be deleted because a database constraint rejected the write.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<AgentWorkSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _dbContext.AgentWorkSessions.AsNoTracking()
                                       .OrderByDescending(entity => entity.UpdatedAtUtc)
                                       .ThenBy(entity => entity.Id)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return [.. sessions.Select(Snapshot)];
    }

    public async Task<AgentWorkSessionSnapshot> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.AgentWorkSessions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken).ConfigureAwait(false)
                      ?? throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
        return Snapshot(session);
    }

    public async Task<AgentWorkSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.AgentWorkSessions.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.ConversationId == conversationId, cancellationToken)
                                      .ConfigureAwait(false);
        return session is null ? null : Snapshot(session);
    }

    public async Task<int> ReconcileRunningSessionsAsync(string sanitizedReason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessions = await _dbContext.AgentWorkSessions.Where(entity => entity.Status == AgentWorkSessionStatus.Running
                                                                              || entity.Status == AgentWorkSessionStatus.WaitingForApproval
                                                                              || entity.Status == AgentWorkSessionStatus.WaitingForInput)
                                           .OrderBy(entity => entity.CreatedAtUtc)
                                           .ThenBy(entity => entity.Id)
                                           .ToListAsync(cancellationToken)
                                           .ConfigureAwait(false);
            if (sessions.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var now = Now();
            foreach (var session in sessions)
            {
                session.Status = AgentWorkSessionStatus.Interrupted;
                session.Version++;
                session.UpdatedAtUtc = now;
                AddEvent(session, "SessionInterrupted", AgentWorkSessionStatus.Interrupted.ToString(), operationId: null, ReasonDetail(sanitizedReason));
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return sessions.Count;
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new WorkSessionConcurrencyException("A concurrent writer won the race before the interrupted sessions were reconciled.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }
}
