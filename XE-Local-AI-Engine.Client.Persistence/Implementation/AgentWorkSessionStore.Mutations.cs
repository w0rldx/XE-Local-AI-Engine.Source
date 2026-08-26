namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class AgentWorkSessionStore
{
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
                                     ?? throw new WorkSessionNotFoundException($"Work session finding '{supersedesId}' was not found on session '{session.Id}'.");
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
                   ?? throw new WorkSessionNotFoundException($"Work session task '{change.TaskId}' was not found on session '{session.Id}'.");

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
            throw new WorkSessionNotFoundException($"Work session task '{taskId}' was not found on session '{sessionId}'.");
        }
    }

}
