namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
    public async Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCreate(command);

        return await ExecuteOperationAsync(command.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                if (await _dbContext.DevelopmentProjects.AnyAsync(entity => entity.Id == command.ProjectId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DevelopmentConcurrencyException($"Development project '{command.ProjectId}' already exists.");
                }

                var now = Now();
                var project = new DevelopmentProject
                {
                    Id = command.ProjectId,
                    Objective = Utf8(command.Objective),
                    SelectedFolderId = command.SelectedFolderId,
                    RepositoryIdentityHash = command.RepositoryIdentityHash,
                    BaseBranch = command.BaseBranch,
                    Status = DevelopmentProjectStatus.Active,
                    EgressPolicy = command.EgressPolicy,
                    CoderModelId = command.CoderModelId,
                    ReviewerModelId = command.ReviewerModelId,
                    MaxTokens = command.MaxTokens,
                    MaxDurationSeconds = command.MaxDurationSeconds,
                    CommandProfileJson = command.CommandProfileJson,
                    ConfigurationVersion = command.ConfigurationVersion,
                    TrustedRepositoryAcknowledged = command.TrustedRepositoryAcknowledged,
                    TrustedRepositoryPolicyVersion = command.TrustedRepositoryPolicyVersion,
                    TrustedRepositoryAcknowledgedAtUtc = command.TrustedRepositoryAcknowledgedAtUtc,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Version = 1
                };
                var task = new DevelopmentTask
                {
                    Id = command.TaskId,
                    ProjectId = command.ProjectId,
                    Title = Utf8(command.Title),
                    Requirements = Utf8(command.Requirements),
                    AcceptanceCriteriaJson = Utf8(command.AcceptanceCriteriaJson),
                    Status = DevelopmentTaskStatus.Planned,
                    MaxReviewRounds = command.MaxReviewRounds,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Version = 1
                };

                _dbContext.DevelopmentProjects.Add(project);
                _dbContext.DevelopmentTasks.Add(task);
                return await AddEventAsync(command.ProjectId,
                    command.TaskId,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ProjectCreated",
                    "Created",
                    DevelopmentTaskStatus.Planned.ToString(),
                    version: 1,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> CreateTaskAsync(DevelopmentCreateTaskCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTask(command.Title, command.Requirements, command.AcceptanceCriteriaJson, command.MaxReviewRounds);

        return await ExecuteOperationAsync(command.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                // Checked rather than left to the foreign key: the node connection runs without PRAGMA foreign_keys, so
                // a task named against a project that does not exist would be inserted and then be unreachable.
                if (!await _dbContext.DevelopmentProjects.AnyAsync(entity => entity.Id == command.ProjectId, cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Development project '{command.ProjectId}' was not found.");
                }

                var now = Now();
                _dbContext.DevelopmentTasks.Add(new DevelopmentTask
                {
                    Id = command.TaskId,
                    ProjectId = command.ProjectId,
                    Title = Utf8(command.Title),
                    Requirements = Utf8(command.Requirements),
                    AcceptanceCriteriaJson = Utf8(command.AcceptanceCriteriaJson),
                    Status = DevelopmentTaskStatus.Planned,
                    MaxReviewRounds = command.MaxReviewRounds,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Version = 1
                });
                return await AddEventAsync(command.ProjectId,
                    command.TaskId,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "TaskCreated",
                    "Created",
                    DevelopmentTaskStatus.Planned.ToString(),
                    version: 1,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.ModelId, "modelId");
        EnsureNotBlank(command.Provider, "provider");

        var projectId = await ProjectIdForTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);
        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == command.TaskId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                EnsureAttemptMayStart(task, command.Role);

                if (command.PredecessorAttemptId is { } predecessorId)
                {
                    var predecessor = await _dbContext.DevelopmentAttempts.SingleOrDefaultAsync(entity => entity.Id == predecessorId && entity.TaskId == task.Id, cancellationToken)
                                                      .ConfigureAwait(false);
                    if (predecessor?.Status != DevelopmentAttemptStatus.Interrupted)
                    {
                        throw new DevelopmentInvalidTransitionException("A replacement attempt must reference an interrupted predecessor on the same task.");
                    }
                }

                var now = Now();
                var attempt = new DevelopmentAttempt
                {
                    Id = command.AttemptId,
                    TaskId = command.TaskId,
                    PredecessorAttemptId = command.PredecessorAttemptId,
                    Role = command.Role,
                    ModelId = command.ModelId,
                    Provider = command.Provider,
                    Status = DevelopmentAttemptStatus.Running,
                    StartedAtUtc = now,
                    StartOperationId = command.OperationId,
                    CommandProfileJson = await ResolveAttemptCommandProfileAsync(projectId, task.Id, command.Role, cancellationToken).ConfigureAwait(false),
                    Version = 1
                };
                _dbContext.DevelopmentAttempts.Add(attempt);

                if (command.Role == DevelopmentAttemptRole.Coder && task.Status != DevelopmentTaskStatus.InProgress)
                {
                    task.Status = DevelopmentTaskStatus.InProgress;
                    task.ApprovedSubjectHash = null;

                    // The sentence that asked for this round stops being the CURRENT one the moment the round starts.
                    // This is the choke point every coder round goes through to reach InProgress without passing
                    // through TransitionTaskAsync — which already clears the column for every target status of its own
                    // — so it is the one place the rework reason could survive the round it asked for. It did: the
                    // Development overview renders blocked_reason with no status gate, so a task actively being
                    // reworked showed the gate failure or the operator's change request that started it. The cost is
                    // named and accepted: the reason leaves the overview when the round starts rather than lingering
                    // until the next verdict, and the event timeline keeps it either way.
                    task.BlockedReason = null;
                    await _dbContext.DevelopmentArtifacts
                                    .Where(entity => entity.TaskId == task.Id
                                                     && entity.IsValid
                                                     && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                         || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                    .ConfigureAwait(false);
                }

                task.UpdatedAtUtc = now;
                task.Version++;

                return await AddEventAsync(projectId,
                    task.Id,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "AttemptStarted",
                    "Started",
                    DevelopmentAttemptStatus.Running.ToString(),
                    attempt.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The command profile to freeze onto a new attempt.
    ///     <para>
    ///         A Coder attempt takes the project's profile as it stands right now. A Reviewer attempt instead inherits
    ///         the profile of the latest succeeded Coder attempt on the same task — the attempt whose result it is
    ///         reviewing. That is what keeps one evidence chain (coder → validation → review → apply) judged under a
    ///         single profile: without it, a profile edit landing between the coder attempt and its review would make
    ///         the reviewer re-run different commands than the ones that produced the patch, and the apply gate would
    ///         then reject on a digest mismatch that describes an edit rather than a defect.
    ///     </para>
    ///     <para>
    ///         Returning null is meaningful and safe: it marks an attempt with no snapshot, and every reader falls back
    ///         to the project's current profile, which is precisely the behaviour before this column existed. That is
    ///         what makes the migration a no-op for rows that predate it.
    ///     </para>
    /// </summary>
    private async Task<string?> ResolveAttemptCommandProfileAsync(Guid projectId,
        Guid taskId,
        DevelopmentAttemptRole role,
        CancellationToken cancellationToken)
    {
        if (role == DevelopmentAttemptRole.Reviewer)
        {
            // Mirrors the (StartedAtUtc, Id) ordering ListAttemptsAsync exposes, so "latest succeeded coder" means the
            // same attempt here as it does to the apply and reviewer gates that select it with LastOrDefault.
            var inherited = await _dbContext.DevelopmentAttempts.AsNoTracking()
                                            .Where(entity => entity.TaskId == taskId
                                                             && entity.Role == DevelopmentAttemptRole.Coder
                                                             && entity.Status == DevelopmentAttemptStatus.Succeeded)
                                            .OrderByDescending(entity => entity.StartedAtUtc)
                                            .ThenByDescending(entity => entity.Id)
                                            .Select(entity => entity.CommandProfileJson)
                                            .FirstOrDefaultAsync(cancellationToken)
                                            .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(inherited))
            {
                return inherited;
            }
        }

        return await _dbContext.DevelopmentProjects.AsNoTracking()
                               .Where(entity => entity.Id == projectId)
                               .Select(entity => entity.CommandProfileJson)
                               .SingleOrDefaultAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running)
        {
            throw new ArgumentException("Terminalization requires a terminal attempt status.", nameof(command));
        }

        var (projectId, taskId) = await OwnershipForAttemptAsync(command.AttemptId, cancellationToken).ConfigureAwait(false);
        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var attempt = await _dbContext.DevelopmentAttempts.SingleAsync(entity => entity.Id == command.AttemptId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(attempt.Version, command.ExpectedAttemptVersion, "attempt");
                if (attempt.Status is not DevelopmentAttemptStatus.Pending and not DevelopmentAttemptStatus.Running)
                {
                    throw new DevelopmentInvalidTransitionException("A terminal attempt cannot be terminalized again with a new operation.");
                }

                attempt.Status = command.Status;
                attempt.EndedAtUtc = Now();
                attempt.TerminalReason = command.TerminalReason;
                attempt.InputTokens = command.InputTokens;
                attempt.OutputTokens = command.OutputTokens;
                attempt.Version++;

                return await AddEventAsync(projectId,
                    taskId,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "AttemptTerminalized",
                    "Terminalized",
                    attempt.Status.ToString(),
                    attempt.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var projectId = await ProjectIdForTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);

        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == command.TaskId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                EnsureLegalTransition(task.Status, command.TargetStatus);

                // The widening is what PAYS for the edge out of Blocked, so the two are checked together. A rework
                // asked of a task at its round cap without one runs a whole coder attempt — its tokens and its
                // duration — and is stood down again by StartNextActionAsync before it can reach a review, which is
                // exactly the two-second no-op loop this edge exists to end.
                if (task.Status == DevelopmentTaskStatus.Blocked && !command.WidenReviewRounds)
                {
                    throw new DevelopmentInvalidTransitionException("A blocked development task can only be reworked by a retry that widens its review-round cap.");
                }

                if (command.WidenReviewRounds)
                {
                    task.MaxReviewRounds++;
                }

                var now = Now();
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                // A task asked for rework carries WHY it was asked, the same way a stood-down one carries why it was
                // stood down: the caller computes a real sentence for both (DevWorkflowDevTaskExecutor.RequestChangesAsync
                // for the rework), and gating this on Blocked alone discarded it into the event log only. The column is
                // named for the case that came first; every reader of it is gated on Status, so the widening reaches the
                // UI field and nothing that means "blocked". BlockedAtUtc stays Blocked-only — it times a stand-down.
                task.BlockedReason = command.TargetStatus is DevelopmentTaskStatus.Blocked or DevelopmentTaskStatus.ChangesRequested
                    ? command.Reason
                    : null;
                task.BlockedAtUtc = command.TargetStatus == DevelopmentTaskStatus.Blocked ? now : null;
                task.ApprovedSubjectHash = command.ApprovedSubjectHash ?? task.ApprovedSubjectHash;

                // A task asked for rework is not an approved one, so it stops carrying the approved subject the moment
                // it is asked rather than when the next coder attempt starts. Defence in depth: the apply port already
                // refuses anything that is not AwaitingApply before it reads this (DevelopmentApplyService), so the
                // stale hash is inert either way — it is simply state that has stopped being true.
                if (command.TargetStatus is DevelopmentTaskStatus.InProgress or DevelopmentTaskStatus.ChangesRequested)
                {
                    task.ApprovedSubjectHash = null;
                }

                // The evidence invalidation stays on InProgress alone: it is the hop the NEXT coder attempt makes, so
                // the stale validation and review reports are marked before anything can read them as current.
                if (command.TargetStatus == DevelopmentTaskStatus.InProgress)
                {
                    await _dbContext.DevelopmentArtifacts
                                    .Where(entity => entity.TaskId == task.Id
                                                     && entity.IsValid
                                                     && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                         || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                    .ConfigureAwait(false);
                }

                if (command.TargetStatus == DevelopmentTaskStatus.InReview)
                {
                    if (task.CurrentReviewRound >= task.MaxReviewRounds)
                    {
                        throw new DevelopmentInvalidTransitionException("The configured maximum number of rounds has been reached.");
                    }

                    task.CurrentReviewRound++;
                }

                return await AddEventAsync(projectId,
                    task.Id,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "TaskTransitioned",

                    // The outcome, not the event type and not a new member of the detail document, is what separates a
                    // person's sentence from a reviewer's. The event type stays "TaskTransitioned" because the task did
                    // transition, the detail document stays byte-for-byte what it was, and the discriminator lands in a
                    // column the snapshot query can filter on in SQL — a flag inside the JSON blob could only be found
                    // by reading rows back and parsing them.
                    command.OperatorDirected ? OperatorTransitionOutcome : "Transitioned",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: command.Reason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.Reason
                        }, JsonOptions)),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
