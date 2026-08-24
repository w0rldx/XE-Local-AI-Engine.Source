namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The durable substrate for agent work sessions. Every mutation takes the session row inside one transaction, which
///     is what makes the single <c>last_sequence</c> counter safe: two writers cannot allocate the same watermark, and
///     neither can skip one.
/// </summary>
internal sealed class AgentWorkSessionStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentWorkSessionStore
{
    private static readonly IReadOnlyDictionary<AgentWorkSessionStatus, HashSet<AgentWorkSessionStatus>> LegalTransitions =
        new Dictionary<AgentWorkSessionStatus, HashSet<AgentWorkSessionStatus>>
        {
            [AgentWorkSessionStatus.Draft] = [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Cancelled],
            [AgentWorkSessionStatus.Running] =
            [
                AgentWorkSessionStatus.Paused,
                AgentWorkSessionStatus.WaitingForInput,
                AgentWorkSessionStatus.WaitingForApproval,
                AgentWorkSessionStatus.Completed,
                AgentWorkSessionStatus.Failed,
                AgentWorkSessionStatus.Cancelled,
                AgentWorkSessionStatus.Interrupted
            ],
            [AgentWorkSessionStatus.Paused] = [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Cancelled],
            [AgentWorkSessionStatus.WaitingForInput] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Cancelled, AgentWorkSessionStatus.Interrupted],
            [AgentWorkSessionStatus.WaitingForApproval] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Cancelled, AgentWorkSessionStatus.Interrupted],
            [AgentWorkSessionStatus.Interrupted] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Failed, AgentWorkSessionStatus.Cancelled]
        };

    private static readonly HashSet<AgentWorkSessionStatus> TerminalStatuses =
        [AgentWorkSessionStatus.Completed, AgentWorkSessionStatus.Failed, AgentWorkSessionStatus.Cancelled];

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
                      ?? throw new KeyNotFoundException($"Work session '{sessionId}' was not found.");
        return Snapshot(session);
    }

    public async Task<AgentWorkSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.AgentWorkSessions.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.ConversationId == conversationId, cancellationToken)
                                      .ConfigureAwait(false);
        return session is null ? null : Snapshot(session);
    }

    public async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ListTasksAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
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

    public async Task<WorkSessionArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.AgentWorkSessionArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                                       .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Work session artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }

    public async Task<WorkSessionCheckpointSnapshot?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var checkpoint = await _dbContext.AgentWorkSessionCheckpoints.AsNoTracking()
                                         .Where(entity => entity.SessionId == sessionId)
                                         .OrderByDescending(entity => entity.Sequence)
                                         .FirstOrDefaultAsync(cancellationToken)
                                         .ConfigureAwait(false);
        return checkpoint is null ? null : CheckpointSnapshot(checkpoint);
    }

    public Task<WorkSessionMutationResult> ApplyPlanAsync(ApplyWorkPlanCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Changes.Count == 0)
        {
            throw new ArgumentException("A work plan change set cannot be empty.", nameof(command));
        }

        return ExecuteMutationAsync(command.SessionId,
            command.ExpectedVersion,
            command.OperationId,
            async session =>
            {
                foreach (var change in command.Changes)
                {
                    await ApplyPlanChangeAsync(session, command.Origin, change, cancellationToken).ConfigureAwait(false);
                }

                return new MutationOutcome("WorkPlanApplied", $"{command.Changes.Count} change(s)", DetailJson: null);
            },
            cancellationToken);
    }

    public Task<WorkSessionMutationResult> AppendFindingAsync(AppendWorkSessionFindingCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Text, nameof(command.Text));

        return ExecuteMutationAsync(command.SessionId,
            command.ExpectedVersion,
            command.OperationId,
            async session =>
            {
                if (command.TaskId is { } taskId)
                {
                    await EnsureTaskBelongsAsync(session.Id, taskId, cancellationToken).ConfigureAwait(false);
                }

                if (command.SupersedesFindingId is { } supersedesId)
                {
                    var superseded = await _dbContext.AgentWorkSessionFindings
                                                     .SingleOrDefaultAsync(entity => entity.Id == supersedesId && entity.SessionId == session.Id, cancellationToken)
                                                     .ConfigureAwait(false)
                                     ?? throw new KeyNotFoundException($"Work session finding '{supersedesId}' was not found on session '{session.Id}'.");
                    superseded.Superseded = true;
                    superseded.Sequence = NextSequence(session);
                }

                _dbContext.AgentWorkSessionFindings.Add(new AgentWorkSessionFinding
                {
                    Id = command.FindingId,
                    SessionId = session.Id,
                    TaskId = command.TaskId,
                    Sequence = NextSequence(session),
                    Kind = command.Kind,
                    Text = Utf8(command.Text),
                    SourceRef = Utf8OrNull(command.SourceRef),
                    CreatedStep = session.StepCount,
                    Superseded = false
                });
                return new MutationOutcome("FindingRecorded", command.Kind.ToString(), DetailJson: null);
            },
            cancellationToken);
    }

    public Task<WorkSessionMutationResult> AppendArtifactAsync(AppendWorkSessionArtifactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.MediaType, nameof(command.MediaType));
        EnsureNotBlank(command.ContentSha256, nameof(command.ContentSha256));
        EnsureNotBlank(command.ManagedReference, nameof(command.ManagedReference));
        if (command.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "An artifact size cannot be negative.");
        }

        return ExecuteMutationAsync(command.SessionId,
            command.ExpectedVersion,
            command.OperationId,
            async session =>
            {
                if (await _dbContext.AgentWorkSessionArtifacts.AnyAsync(entity => entity.Id == command.ArtifactId, cancellationToken).ConfigureAwait(false))
                {
                    throw new WorkSessionConcurrencyException($"Work session artifact '{command.ArtifactId}' already exists.");
                }

                var existing = await _dbContext.AgentWorkSessionArtifacts
                                               .SingleOrDefaultAsync(entity => entity.SessionId == session.Id && entity.Name == command.Name, cancellationToken)
                                               .ConfigureAwait(false);
                byte[]? detail = null;
                Guid? supersededId = null;
                if (existing is not null)
                {
                    // Saving under an existing name replaces it. The row goes now; the bytes are the caller's to sweep
                    // after the commit, which is why the reference travels on both the event and the result.
                    supersededId = existing.Id;
                    detail = Utf8(JsonSerializer.Serialize(new ArtifactReplacementDetail(existing.Id, existing.ManagedReference)));
                    _dbContext.AgentWorkSessionArtifacts.Remove(existing);
                }

                _dbContext.AgentWorkSessionArtifacts.Add(new AgentWorkSessionArtifact
                {
                    Id = command.ArtifactId,
                    SessionId = session.Id,
                    Sequence = NextSequence(session),
                    Kind = command.Kind,
                    Name = command.Name,
                    MediaType = command.MediaType,
                    ContentSha256 = command.ContentSha256,
                    SizeBytes = command.SizeBytes,
                    IsValid = true,
                    ManagedReference = command.ManagedReference,
                    CreatedStep = session.StepCount
                });
                return new MutationOutcome("ArtifactSaved", command.Kind.ToString(), detail, supersededId);
            },
            cancellationToken);
    }

    public Task<WorkSessionMutationResult> AppendCheckpointAsync(AppendWorkSessionCheckpointCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.StateJson, nameof(command.StateJson));
        if (command.Step < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A checkpoint step cannot be negative.");
        }

        return ExecuteMutationAsync(command.SessionId,
            command.ExpectedVersion,
            command.OperationId,
            session =>
            {
                _dbContext.AgentWorkSessionCheckpoints.Add(new AgentWorkSessionCheckpoint
                {
                    Id = command.CheckpointId,
                    SessionId = session.Id,
                    Sequence = NextSequence(session),
                    Step = command.Step,
                    Summary = Utf8OrNull(command.Summary),
                    StateJson = Utf8(command.StateJson),
                    CreatedAtUtc = Now()
                });
                session.LastCheckpointId = command.CheckpointId;
                return Task.FromResult(new MutationOutcome("CheckpointRecorded", command.Step.ToString(CultureInfo.InvariantCulture), DetailJson: null));
            },
            cancellationToken);
    }

    public Task<WorkSessionMutationResult> AppendEventAsync(AppendWorkSessionEventCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.EventType, nameof(command.EventType));

        return ExecuteMutationAsync(command.SessionId,
            command.ExpectedVersion,
            command.OperationId,
            _ => Task.FromResult(new MutationOutcome(command.EventType, command.Outcome, Utf8OrNull(command.DetailJson))),
            cancellationToken);
    }

    public Task<WorkSessionMutationResult> AdvanceStepAsync(Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return ExecuteMutationAsync(sessionId,
            expectedVersion,
            operationId: null,
            session =>
            {
                session.StepCount++;
                return Task.FromResult(new MutationOutcome("StepAdvanced", session.StepCount.ToString(CultureInfo.InvariantCulture), DetailJson: null));
            },
            cancellationToken);
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

    private async Task ApplyPlanChangeAsync(AgentWorkSession session,
        AgentWorkSessionTaskOrigin origin,
        WorkPlanTaskChange change,
        CancellationToken cancellationToken)
    {
        if (change.ParentTaskId is { } parentTaskId)
        {
            if (parentTaskId == change.TaskId)
            {
                throw new ArgumentException($"Work plan task '{change.TaskId}' cannot be its own parent.", nameof(change));
            }

            await EnsureTaskBelongsAsync(session.Id, parentTaskId, cancellationToken).ConfigureAwait(false);
        }

        if (change.Operation == WorkPlanTaskOperation.Add)
        {
            EnsureNotBlank(change.Title, nameof(change.Title));
            if (await _dbContext.AgentWorkSessionTasks.AnyAsync(entity => entity.Id == change.TaskId, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkSessionConcurrencyException($"Work session task '{change.TaskId}' already exists.");
            }

            _dbContext.AgentWorkSessionTasks.Add(new AgentWorkSessionTask
            {
                Id = change.TaskId,
                SessionId = session.Id,
                ParentTaskId = change.ParentTaskId,
                Sequence = NextSequence(session),
                Title = Utf8(change.Title!),
                Detail = Utf8OrNull(change.Detail),
                Status = change.Status ?? AgentWorkSessionTaskStatus.Planned,
                BlockedReason = Utf8OrNull(change.BlockedReason),
                Origin = origin,
                CreatedStep = session.StepCount,
                UpdatedStep = session.StepCount
            });
            TrackCurrentTask(session, change.TaskId, change.Status ?? AgentWorkSessionTaskStatus.Planned);
            return;
        }

        var task = await _dbContext.AgentWorkSessionTasks.SingleOrDefaultAsync(entity => entity.Id == change.TaskId && entity.SessionId == session.Id, cancellationToken)
                                   .ConfigureAwait(false)
                   ?? throw new KeyNotFoundException($"Work session task '{change.TaskId}' was not found on session '{session.Id}'.");

        if (change.ParentTaskId is { } newParent)
        {
            task.ParentTaskId = newParent;
        }

        if (change.Title is not null)
        {
            EnsureNotBlank(change.Title, nameof(change.Title));
            task.Title = Utf8(change.Title);
        }

        if (change.Detail is not null)
        {
            task.Detail = Utf8(change.Detail);
        }

        if (change.BlockedReason is not null)
        {
            task.BlockedReason = Utf8(change.BlockedReason);
        }

        task.Status = change.Operation switch
        {
            WorkPlanTaskOperation.Complete => AgentWorkSessionTaskStatus.Done,
            WorkPlanTaskOperation.Drop => AgentWorkSessionTaskStatus.Dropped,
            _ => change.Status ?? task.Status
        };

        TrackCurrentTask(session, task.Id, task.Status);

        // The change watermark moves on every mutation, not only on insert, so a ?sinceSeq= list replays updates too.
        task.Sequence = NextSequence(session);
        task.UpdatedStep = session.StepCount;
    }

    /// <summary>
    ///     Keeps the session's current-task pointer true as the plan changes. A task moved to <c>Active</c> becomes the
    ///     current one; the pointer is cleared when the task it names is finished or dropped.
    ///     <para>
    ///         Without this the pointer only ever moved on a status transition, so it went stale for the rest of a
    ///         multi-step run every time the agent switched tasks — and every reader outside the step loop (the REST
    ///         detail, the checkpoint state) read the stale value.
    ///     </para>
    /// </summary>
    private static void TrackCurrentTask(AgentWorkSession session, Guid taskId, AgentWorkSessionTaskStatus status)
    {
        if (status == AgentWorkSessionTaskStatus.Active)
        {
            session.CurrentTaskId = taskId;
        }
        else if (session.CurrentTaskId == taskId && status is AgentWorkSessionTaskStatus.Done or AgentWorkSessionTaskStatus.Dropped)
        {
            session.CurrentTaskId = null;
        }
    }

    private async Task EnsureTaskBelongsAsync(Guid sessionId, Guid taskId, CancellationToken cancellationToken)
    {
        // Checked here rather than left to the foreign key: cascades and restrictions do not fire on this connection.
        if (!await _dbContext.AgentWorkSessionTasks.AnyAsync(entity => entity.Id == taskId && entity.SessionId == sessionId, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Work session task '{taskId}' was not found on session '{sessionId}'.");
        }
    }

    private async Task<WorkSessionMutationResult> ExecuteMutationAsync(Guid sessionId,
        long expectedVersion,
        Guid? operationId,
        Func<AgentWorkSession, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken)
    {
        // Query-first, never insert-then-catch: a caught unique-index violation leaves an Added entity in the change
        // tracker that every later write in the same scope would trip over.
        if (operationId is { } preflight && await FindOperationAsync(sessionId, preflight, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return recorded;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationId is { } inTransaction && await FindOperationAsync(sessionId, inTransaction, cancellationToken).ConfigureAwait(false) is { } alreadyRecorded)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return alreadyRecorded;
            }

            var session = await _dbContext.AgentWorkSessions.SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new KeyNotFoundException($"Work session '{sessionId}' was not found.");
            EnsureVersion(session, expectedVersion);

            var outcome = await mutate(session).ConfigureAwait(false);
            var sequence = AddEvent(session, outcome.EventType, outcome.Outcome, operationId, outcome.DetailJson);
            session.Version++;
            session.UpdatedAtUtc = Now();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkSessionMutationResult(sessionId, sequence, session.StepCount, session.Version, session.Status, session.CurrentTaskId, outcome.SupersededArtifactId);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new WorkSessionConcurrencyException("A concurrent writer won the race before the work session mutation committed.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     The event already recorded for this operation, rebuilt against the session row as it stands now — a replayed
    ///     step wants the version it should continue from, not the one the first attempt saw.
    /// </summary>
    private async Task<WorkSessionMutationResult?> FindOperationAsync(Guid sessionId, Guid operationId, CancellationToken cancellationToken)
    {
        var recorded = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.SessionId == sessionId && entity.OperationId == operationId, cancellationToken)
                                       .ConfigureAwait(false);
        if (recorded is null)
        {
            return null;
        }

        var session = await _dbContext.AgentWorkSessions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken).ConfigureAwait(false);
        return session is null
            ? null
            : new WorkSessionMutationResult(sessionId, recorded.Sequence, recorded.Step, session.Version, session.Status, session.CurrentTaskId);
    }

    private long AddEvent(AgentWorkSession session, string eventType, string? outcome, Guid? operationId, byte[]? detailJson)
    {
        var sequence = NextSequence(session);
        _dbContext.AgentWorkSessionEvents.Add(new AgentWorkSessionEvent
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Sequence = sequence,
            Step = session.StepCount,
            EventType = eventType,
            DetailJson = detailJson,
            OperationId = operationId,
            Outcome = outcome,
            OccurredAtUtc = Now()
        });
        return sequence;
    }

    private static void ApplyStatus(AgentWorkSession session, AgentWorkSessionStatus target, Guid? currentTaskId)
    {
        if (!LegalTransitions.TryGetValue(session.Status, out var allowed) || !allowed.Contains(target))
        {
            throw new WorkSessionInvalidTransitionException($"Work session transition {session.Status} -> {target} is not legal.");
        }

        session.Status = target;
        if (TerminalStatuses.Contains(target))
        {
            session.CurrentTaskId = null;
        }
        else if (currentTaskId is { } taskId)
        {
            session.CurrentTaskId = taskId;
        }
    }

    private static long NextSequence(AgentWorkSession session) =>
        ++session.LastSequence;

    private static void EnsureVersion(AgentWorkSession session, long expectedVersion)
    {
        if (expectedVersion == WorkSessionVersions.Any || session.Version == expectedVersion)
        {
            return;
        }

        throw new WorkSessionConcurrencyException($"The work session version is stale (expected {expectedVersion}, current {session.Version}).");
    }

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
    }

    private static AgentWorkSessionSnapshot Snapshot(AgentWorkSession session) =>
        new(session.Id,
            session.Title,
            Text(session.Objective),
            session.Kind,
            session.Status,
            session.AgentDefinitionId,
            session.ConversationId,
            session.CurrentTaskId,
            session.StepCount,
            session.LastCheckpointId,
            session.LastSequence,
            session.ConfigVersion,
            session.CreatedAtUtc,
            session.UpdatedAtUtc,
            session.Version);

    private static WorkSessionArtifactSnapshot ArtifactSnapshot(AgentWorkSessionArtifact artifact) =>
        new(artifact.Id,
            artifact.SessionId,
            artifact.Sequence,
            artifact.Kind,
            artifact.Name,
            artifact.MediaType,
            artifact.ContentSha256,
            artifact.SizeBytes,
            artifact.IsValid,
            artifact.ManagedReference,
            artifact.CreatedStep);

    private static WorkSessionCheckpointSnapshot CheckpointSnapshot(AgentWorkSessionCheckpoint checkpoint) =>
        new(checkpoint.Id,
            checkpoint.SessionId,
            checkpoint.Sequence,
            checkpoint.Step,
            TextOrNull(checkpoint.Summary),
            Text(checkpoint.StateJson),
            checkpoint.CreatedAtUtc);

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason)));

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    private sealed record MutationOutcome(string EventType, string? Outcome, byte[]? DetailJson, Guid? SupersededArtifactId = null);

    private sealed record ArtifactReplacementDetail(Guid SupersededArtifactId, string SupersededManagedReference);

    private sealed record ReasonDetailPayload(string Reason);
}
