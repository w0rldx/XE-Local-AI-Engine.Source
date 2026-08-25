namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IDevelopmentStore
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

                var now = Now();
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                task.BlockedReason = command.TargetStatus == DevelopmentTaskStatus.Blocked ? command.Reason : null;
                task.BlockedAtUtc = command.TargetStatus == DevelopmentTaskStatus.Blocked ? now : null;
                task.ApprovedSubjectHash = command.ApprovedSubjectHash ?? task.ApprovedSubjectHash;
                if (command.TargetStatus == DevelopmentTaskStatus.InProgress)
                {
                    task.ApprovedSubjectHash = null;
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
                        throw new DevelopmentInvalidTransitionException("The configured maximum review rounds has been reached.");
                    }

                    task.CurrentReviewRound++;
                }

                return await AddEventAsync(projectId,
                    task.Id,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "TaskTransitioned",
                    "Transitioned",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: command.Reason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.Reason
                        })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> StartValidationAsync(DevelopmentStartValidationCommand command, CancellationToken cancellationToken = default)
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
                if (task.Status != DevelopmentTaskStatus.InProgress)
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation requires an in-progress Development task.");
                }

                if (await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.TaskId == task.Id
                                                                            && (entity.Status == DevelopmentAttemptStatus.Pending
                                                                                || entity.Status == DevelopmentAttemptStatus.Running),
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation cannot overlap an active Development attempt.");
                }

                var latestAttempt = await _dbContext.DevelopmentAttempts
                                                    .Where(entity => entity.TaskId == task.Id)
                                                    .OrderByDescending(entity => entity.StartedAtUtc)
                                                    .ThenByDescending(entity => entity.Id)
                                                    .FirstOrDefaultAsync(cancellationToken)
                                                    .ConfigureAwait(false);
                if (latestAttempt is null
                    || latestAttempt.Role != DevelopmentAttemptRole.Coder
                    || latestAttempt.Status != DevelopmentAttemptStatus.Succeeded)
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation requires the latest Development attempt to be a successful coder attempt.");
                }

                task.Status = DevelopmentTaskStatus.Validation;
                task.UpdatedAtUtc = Now();
                task.Version++;

                return await AddEventAsync(projectId,
                    task.Id,
                    latestAttempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ValidationStarted",
                    "Started",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> InvalidateEvidenceAsync(DevelopmentInvalidateEvidenceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.SanitizedReason, "sanitizedReason");
        var projectId = await ProjectIdForTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);

        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == command.TaskId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status is not (DevelopmentTaskStatus.Validation
                    or DevelopmentTaskStatus.InReview
                    or DevelopmentTaskStatus.AwaitingApply))
                {
                    throw new DevelopmentInvalidTransitionException("Only validation, review, or approved evidence can be invalidated.");
                }

                task.Status = DevelopmentTaskStatus.InProgress;
                task.UpdatedAtUtc = Now();
                task.ApprovedSubjectHash = null;
                task.BlockedReason = null;
                task.BlockedAtUtc = null;
                task.Version++;
                await _dbContext.DevelopmentArtifacts
                                .Where(entity => entity.TaskId == task.Id
                                                 && entity.IsValid
                                                 && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                     || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                .ConfigureAwait(false);

                return await AddEventAsync(projectId,
                    task.Id,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "EvidenceInvalidated",
                    "Invalidated",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: Utf8(JsonSerializer.Serialize(new
                    {
                        reason = command.SanitizedReason
                    })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> FinalizeValidationAsync(DevelopmentFinalizeValidationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command.Artifact);
        if (command.Artifact.Kind != DevelopmentArtifactKind.ValidationReport
            || command.TargetStatus is not (DevelopmentTaskStatus.InReview or DevelopmentTaskStatus.InProgress))
        {
            throw new ArgumentException("Validation finalization requires a validation artifact and a review or rework target.", nameof(command));
        }

        return await ExecuteOperationAsync(command.Artifact.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.Artifact.TaskId
                                                                                            && entity.ProjectId == command.Artifact.ProjectId,
                                               cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.Artifact.TaskId}' was not found.");
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.Validation)
                {
                    throw new DevelopmentInvalidTransitionException("Only an active deterministic validation can be finalized.");
                }

                var attemptId = command.Artifact.AttemptId
                                ?? throw new DevelopmentInvalidTransitionException("A validation artifact must identify its coder attempt.");
                var attempt = await _dbContext.DevelopmentAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id,
                                                  cancellationToken)
                                              .ConfigureAwait(false)
                              ?? throw new DevelopmentInvalidTransitionException("The validation artifact coder attempt was not found on the task.");
                if (attempt.Role != DevelopmentAttemptRole.Coder || attempt.Status != DevelopmentAttemptStatus.Succeeded)
                {
                    throw new DevelopmentInvalidTransitionException("A validation artifact must originate from a successful coder attempt.");
                }

                var artifact = BuildArtifact(command.Artifact);
                _dbContext.DevelopmentArtifacts.Add(artifact);
                var now = Now();
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                task.ApprovedSubjectHash = null;
                if (command.TargetStatus == DevelopmentTaskStatus.InReview)
                {
                    if (task.CurrentReviewRound >= task.MaxReviewRounds)
                    {
                        throw new DevelopmentInvalidTransitionException("The configured maximum review rounds has been reached.");
                    }

                    task.CurrentReviewRound++;
                }
                else
                {
                    artifact.IsValid = false;
                    await _dbContext.DevelopmentArtifacts
                                    .Where(entity => entity.TaskId == task.Id
                                                     && entity.IsValid
                                                     && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                         || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                    .ConfigureAwait(false);
                }

                return await AddEventAsync(command.Artifact.ProjectId,
                    task.Id,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ValidationFinalized",
                    command.TargetStatus == DevelopmentTaskStatus.InReview ? "Passed" : "Failed",
                    task.Status.ToString(),
                    task.Version,
                    artifact.Id,
                    detailJson: command.SanitizedReason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.SanitizedReason
                        })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> FinalizeReviewAsync(DevelopmentFinalizeReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command.Artifact);
        if (command.Artifact.Kind != DevelopmentArtifactKind.ReviewReport
            || command.TargetStatus is not (DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.ChangesRequested))
        {
            throw new ArgumentException("Review finalization requires a review artifact and an apply or rework target.", nameof(command));
        }

        return await ExecuteOperationAsync(command.Artifact.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.Artifact.TaskId
                                                                                            && entity.ProjectId == command.Artifact.ProjectId,
                                               cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.Artifact.TaskId}' was not found.");
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.InReview)
                {
                    throw new DevelopmentInvalidTransitionException("Only an active independent review can be finalized.");
                }

                var attemptId = command.Artifact.AttemptId
                                ?? throw new DevelopmentInvalidTransitionException("A review artifact must identify its reviewer attempt.");
                var attempt = await _dbContext.DevelopmentAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id,
                                                  cancellationToken)
                                              .ConfigureAwait(false)
                              ?? throw new DevelopmentInvalidTransitionException("The review artifact reviewer attempt was not found on the task.");
                EnsureVersion(attempt.Version, command.ExpectedAttemptVersion, "attempt");
                if (attempt.Role != DevelopmentAttemptRole.Reviewer || attempt.Status != DevelopmentAttemptStatus.Running)
                {
                    throw new DevelopmentInvalidTransitionException("A review artifact must originate from the running independent reviewer attempt.");
                }

                var artifact = BuildArtifact(command.Artifact);
                _dbContext.DevelopmentArtifacts.Add(artifact);
                var now = Now();
                attempt.Status = DevelopmentAttemptStatus.Succeeded;
                attempt.EndedAtUtc = now;
                attempt.TerminalReason = null;
                attempt.InputTokens = command.InputTokens;
                attempt.OutputTokens = command.OutputTokens;
                attempt.Version++;
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                task.ApprovedSubjectHash = command.TargetStatus == DevelopmentTaskStatus.AwaitingApply
                    ? command.ApprovedSubjectHash
                    : null;

                return await AddEventAsync(command.Artifact.ProjectId,
                    task.Id,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ReviewFinalized",
                    command.TargetStatus == DevelopmentTaskStatus.AwaitingApply ? "Approved" : "ChangesRequested",
                    task.Status.ToString(),
                    task.Version,
                    artifact.Id,
                    detailJson: command.SanitizedReason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.SanitizedReason
                        })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command);

        return await ExecuteOperationAsync(command.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.TaskId && entity.ProjectId == command.ProjectId, cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.TaskId}' was not found.");
                if (command.AttemptId is { } attemptId && !await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id, cancellationToken)
                                                                           .ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
                }

                var artifact = BuildArtifact(command);
                _dbContext.DevelopmentArtifacts.Add(artifact);

                return await AddEventAsync(command.ProjectId,
                    command.TaskId,
                    command.AttemptId,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ArtifactAttached",
                    "Attached",
                    command.Kind.ToString(),
                    version: 1,
                    command.ArtifactId,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> RecordWorkspaceSecretsAsync(Guid taskId,
        Guid attemptId,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryRelativePaths);
        if (repositoryRelativePaths.Count == 0)
        {
            throw new ArgumentException("A workspace-secret event must name at least one path.", nameof(repositoryRelativePaths));
        }

        var projectId = await ProjectIdForTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

        // Keyed on the ATTEMPT id rather than a fresh operation id, which is what makes it idempotent: a task's
        // workspace is prepared once by the coder and again by validation, and the same finding recorded twice would
        // read as two separate discoveries.
        return await ExecuteOperationAsync(projectId,
            attemptId,
            WorkspaceSecretsOperationPhase,
            async () =>
            {
                if (!await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.Id == attemptId && entity.TaskId == taskId, cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
                }

                return await AddEventAsync(projectId,
                    taskId,
                    attemptId,
                    attemptId,
                    WorkspaceSecretsOperationPhase,
                    "WorkspaceSecretsDetected",
                    "Detected",
                    repositoryRelativePaths.Count.ToString(CultureInfo.InvariantCulture),
                    version: 1,
                    artifactId: null,
                    JsonSerializer.SerializeToUtf8Bytes(repositoryRelativePaths),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReconcileRunningAttemptsAsync(string sanitizedReason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await _dbContext.DevelopmentAttempts.Where(entity => entity.Status == DevelopmentAttemptStatus.Running)
                                       .OrderBy(entity => entity.StartedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        if (attempts.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var attempt in attempts)
        {
            var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == attempt.TaskId, cancellationToken).ConfigureAwait(false);
            var operationId = attempt.Id;
            if (await FindOperationCoreAsync(task.ProjectId, operationId, StartupOperationPhase, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            attempt.Status = DevelopmentAttemptStatus.Interrupted;
            attempt.EndedAtUtc = Now();
            attempt.TerminalReason = sanitizedReason;
            attempt.Version++;
            await AddEventAsync(task.ProjectId,
                task.Id,
                attempt.Id,
                operationId,
                StartupOperationPhase,
                "AttemptInterrupted",
                "Interrupted",
                DevelopmentAttemptStatus.Interrupted.ToString(),
                attempt.Version,
                artifactId: null,
                detailJson: null,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return attempts.Count;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new DevelopmentConcurrencyException("Concurrent startup reconciliation won the optimistic concurrency race.", exception);
        }
    }

    public async Task<int> ReconcileIncompleteValidationsAsync(string sanitizedReason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        var validations = await _dbContext.DevelopmentTasks.AsNoTracking()
                                          .Where(entity => entity.Status == DevelopmentTaskStatus.Validation)
                                          .Select(entity => new
                                          {
                                              entity.Id,
                                              entity.Version
                                          })
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);
        var reconciled = 0;
        foreach (var validation in validations)
        {
            try
            {
                _ = await InvalidateEvidenceAsync(new DevelopmentInvalidateEvidenceCommand(validation.Id,
                            validation.Id,
                            validation.Version,
                            sanitizedReason),
                        cancellationToken)
                    .ConfigureAwait(false);
                reconciled++;
            }
            catch (DevelopmentConcurrencyException)
            {
                // A live operation determined the authoritative task state while startup reconciliation was reading it.
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // The task already left Validation; no recovery mutation remains.
            }
        }

        return reconciled;
    }

    public Task<DevelopmentOperationResult?> FindOperationAsync(Guid projectId, Guid operationId, string phase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return FindOperationCoreAsync(projectId, operationId, phase, cancellationToken);
    }

    public async Task<DevelopmentOperationResult> RecordApplyStartedAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyStarted,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, subject.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.AwaitingApply
                    || string.IsNullOrWhiteSpace(subject.SubjectHash)
                    || !string.Equals(task.ApprovedSubjectHash, subject.SubjectHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DevelopmentInvalidTransitionException("Only the exact independently approved subject can start host apply.");
                }

                var detail = Utf8(JsonSerializer.Serialize(subject));
                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyStarted,
                    "ApplyStarted",
                    "Started",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detail,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> CompleteApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyCompleted,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, subject.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.AwaitingApply)
                {
                    throw new DevelopmentInvalidTransitionException("Only a task awaiting explicit apply can complete.");
                }

                if (!string.Equals(task.ApprovedSubjectHash, subject.SubjectHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DevelopmentInvalidTransitionException("The completed apply subject no longer matches the independently approved subject.");
                }

                task.Status = DevelopmentTaskStatus.Completed;
                task.UpdatedAtUtc = Now();
                task.Version++;
                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyCompleted,
                    "ApplyCompleted",
                    "Completed",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> BlockApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        string sanitizedReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyBlocked,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                if (task.Status != DevelopmentTaskStatus.Blocked)
                {
                    task.Status = DevelopmentTaskStatus.Blocked;
                    task.BlockedReason = sanitizedReason;
                    task.BlockedAtUtc = Now();
                    task.UpdatedAtUtc = task.BlockedAtUtc.Value;
                    task.Version++;
                }

                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyBlocked,
                    "ApplyBlocked",
                    "Blocked",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    Utf8(JsonSerializer.Serialize(new
                    {
                        reason = sanitizedReason
                    })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DevelopmentEvents.AsNoTracking()
                               .Where(entity => entity.ProjectId == projectId)
                               .OrderBy(entity => entity.Sequence)
                               .Select(entity => new DevelopmentEventSnapshot(entity.Id,
                                   entity.ProjectId,
                                   entity.TaskId,
                                   entity.AttemptId,
                                   entity.Sequence,
                                   entity.EventType,
                                   entity.OccurredAtUtc,
                                   entity.OperationId,
                                   entity.OperationPhase,
                                   entity.Outcome))
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<DevelopmentExecutionSnapshot> GetExecutionSnapshotAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        var snapshot = await (from attempt in _dbContext.DevelopmentAttempts.AsNoTracking()
                                 join task in _dbContext.DevelopmentTasks.AsNoTracking() on attempt.TaskId equals task.Id
                                 join project in _dbContext.DevelopmentProjects.AsNoTracking() on task.ProjectId equals project.Id
                                 where attempt.Id == attemptId
                                 select new
                                 {
                                     Project = project,
                                     Task = task,
                                     Attempt = attempt
                                 })
                             .SingleOrDefaultAsync(cancellationToken)
                             .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found.");

        return new DevelopmentExecutionSnapshot(snapshot.Project.Id,
            snapshot.Task.Id,
            snapshot.Attempt.Id,
            snapshot.Project.SelectedFolderId,
            snapshot.Project.RepositoryIdentityHash,
            snapshot.Project.BaseBranch,
            snapshot.Project.EgressPolicy,
            snapshot.Project.ConfigurationVersion,
            snapshot.Project.TrustedRepositoryAcknowledged,
            snapshot.Project.TrustedRepositoryPolicyVersion,
            snapshot.Project.TrustedRepositoryAcknowledgedAtUtc,
            snapshot.Project.MaxTokens,
            snapshot.Project.MaxDurationSeconds,
            Encoding.UTF8.GetString(snapshot.Task.Title),
            Encoding.UTF8.GetString(snapshot.Task.Requirements),
            Encoding.UTF8.GetString(snapshot.Task.AcceptanceCriteriaJson),
            snapshot.Task.Status,
            snapshot.Task.Version,
            snapshot.Attempt.Role,
            snapshot.Attempt.Status,
            snapshot.Attempt.ModelId,
            snapshot.Attempt.Provider,
            snapshot.Attempt.Version,

            // The attempt's own immutable snapshot wins. Falling back to the project only when the attempt has none
            // keeps attempts that predate the column behaving exactly as before, and lets a project whose profile was
            // backfilled after the attempt started still resolve one.
            snapshot.Attempt.CommandProfileJson ?? snapshot.Project.CommandProfileJson);
    }

    public async Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.DevelopmentProjects.AsNoTracking()
                                       .OrderByDescending(entity => entity.UpdatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return projects.Select(ProjectSnapshot).ToArray();
    }

    public async Task<DevelopmentProjectSnapshot> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");
        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentProjectSnapshot> ReconnectProjectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");
        if (project.SelectedFolderId == selectedFolderId)
        {
            return ProjectSnapshot(project);
        }

        if (project.SelectedFolderId is not null)
        {
            throw new DevelopmentConcurrencyException("The Development project is already connected to another selected folder.");
        }

        EnsureVersion(project.Version, expectedVersion, "project");
        project.SelectedFolderId = selectedFolderId;
        project.UpdatedAtUtc = Now();
        project.Version++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DevelopmentConcurrencyException("The Development project changed before its repository could be reconnected.", exception);
        }

        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentProjectSnapshot> BackfillCommandProfileAsync(Guid projectId,
        string commandProfileJson,
        CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(commandProfileJson, nameof(commandProfileJson));
        var project = await _dbContext.DevelopmentProjects
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");

        // Fill-only. An existing profile is the operator-confirmed agreement for the life of the project, so a backfill
        // pass must return it untouched rather than replace it — this is what makes a second pass, or two racing
        // passes, harmless.
        if (!string.IsNullOrWhiteSpace(project.CommandProfileJson))
        {
            return ProjectSnapshot(project);
        }

        project.CommandProfileJson = commandProfileJson;
        project.ConfigurationVersion++;
        project.UpdatedAtUtc = Now();
        project.Version++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DevelopmentConcurrencyException("The Development project changed before its command profile could be backfilled.", exception);
        }

        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentTaskSnapshot> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.DevelopmentTasks.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken)
                                   .ConfigureAwait(false)
                   ?? throw new KeyNotFoundException($"Development task '{taskId}' was not found.");
        return TaskSnapshot(task);
    }

    public async Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.DevelopmentTasks.AsNoTracking()
                                    .Where(entity => entity.ProjectId == projectId)
                                    .OrderBy(entity => entity.CreatedAtUtc)
                                    .ThenBy(entity => entity.Id)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        return tasks.Select(TaskSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DevelopmentAttempts.AsNoTracking()
                               .Where(entity => entity.TaskId == taskId)
                               .OrderBy(entity => entity.StartedAtUtc)
                               .ThenBy(entity => entity.Id)
                               .Select(entity => new DevelopmentAttemptSnapshot(entity.Id,
                                   entity.TaskId,
                                   entity.PredecessorAttemptId,
                                   entity.Role,
                                   entity.ModelId,
                                   entity.Provider,
                                   entity.Status,
                                   entity.StartedAtUtc,
                                   entity.EndedAtUtc,
                                   entity.TerminalReason,
                                   entity.InputTokens,
                                   entity.OutputTokens,
                                   entity.Version))
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                        .Where(entity => entity.TaskId == taskId)
                                        .OrderBy(entity => entity.CreatedAtUtc)
                                        .ThenBy(entity => entity.Id)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return artifacts.Select(ArtifactSnapshot).ToArray();
    }

    public async Task<DevelopmentArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                                       .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Development artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }

    private async Task<DevelopmentOperationResult> ExecuteOperationAsync(Guid projectId,
        Guid operationId,
        string phase,
        Func<Task<DevelopmentOperationResult>> mutation,
        CancellationToken cancellationToken)
    {
        var existing = await FindOperationCoreAsync(projectId, operationId, phase, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        existing = await FindOperationCoreAsync(projectId, operationId, phase, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        try
        {
            var result = await mutation().ConfigureAwait(false);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            existing = await FindOperationCoreAsync(projectId, operationId, phase, CancellationToken.None).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            throw new DevelopmentConcurrencyException("A concurrent Development operation won the database race.", exception);
        }
    }

    private async Task<DevelopmentOperationResult?> FindOperationCoreAsync(Guid projectId,
        Guid operationId,
        string phase,
        CancellationToken cancellationToken)
    {
        var developmentEvent = await _dbContext.DevelopmentEvents.AsNoTracking()
                                               .SingleOrDefaultAsync(entity => entity.ProjectId == projectId
                                                                               && entity.OperationId == operationId
                                                                               && entity.OperationPhase == phase,
                                                   cancellationToken)
                                               .ConfigureAwait(false);
        return developmentEvent?.ResultMetadataJson is not { } payload
            ? null
            : JsonSerializer.Deserialize<DevelopmentOperationResult>(payload);
    }

    private async Task<DevelopmentOperationResult> AddEventAsync(Guid projectId,
        Guid? taskId,
        Guid? attemptId,
        Guid operationId,
        string operationPhase,
        string eventType,
        string outcome,
        string status,
        long version,
        Guid? artifactId,
        byte[]? detailJson,
        CancellationToken cancellationToken)
    {
        var sequence = (await _dbContext.DevelopmentEvents.Where(entity => entity.ProjectId == projectId)
                                        .MaxAsync(entity => (long?)entity.Sequence, cancellationToken)
                                        .ConfigureAwait(false) ?? 0) + 1;
        var result = new DevelopmentOperationResult(projectId,
            taskId,
            attemptId,
            artifactId,
            operationId,
            operationPhase,
            outcome,
            status,
            version,
            sequence);
        _dbContext.DevelopmentEvents.Add(new DevelopmentEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskId = taskId,
            AttemptId = attemptId,
            Sequence = sequence,
            EventType = eventType,
            OccurredAtUtc = Now(),
            DetailJson = detailJson,
            OperationId = operationId,
            OperationPhase = operationPhase,
            Outcome = outcome,
            ResultMetadataJson = JsonSerializer.SerializeToUtf8Bytes(result)
        });
        return result;
    }

    private async Task<Guid> ProjectIdForTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        return await _dbContext.DevelopmentTasks.AsNoTracking()
                               .Where(entity => entity.Id == taskId)
                               .Select(entity => entity.ProjectId)
                               .SingleAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    private async Task<AttemptOwnership> OwnershipForAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var taskId = await _dbContext.DevelopmentAttempts.AsNoTracking()
                                     .Where(entity => entity.Id == attemptId)
                                     .Select(entity => entity.TaskId)
                                     .SingleAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return new AttemptOwnership(await ProjectIdForTaskAsync(taskId, cancellationToken).ConfigureAwait(false), taskId);
    }

    private async Task<DevelopmentTask> LoadApplyTaskAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken)
    {
        return await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == subject.TaskId && entity.ProjectId == subject.ProjectId, cancellationToken)
                               .ConfigureAwait(false)
               ?? throw new KeyNotFoundException($"Development task '{subject.TaskId}' was not found.");
    }

    private static void ValidateCreate(DevelopmentCreateProjectCommand command)
    {
        EnsureNotBlank(command.Objective, "objective");
        EnsureNotBlank(command.RepositoryIdentityHash, "repositoryIdentityHash");
        EnsureNotBlank(command.BaseBranch, "baseBranch");
        EnsureNotBlank(command.Title, "title");
        EnsureNotBlank(command.Requirements, "requirements");
        EnsureNotBlank(command.AcceptanceCriteriaJson, "acceptanceCriteriaJson");
        if (command.MaxReviewRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Maximum review rounds must be positive.");
        }
    }

    private static void EnsureAttemptMayStart(DevelopmentTask task, DevelopmentAttemptRole role)
    {
        var allowed = role == DevelopmentAttemptRole.Reviewer
            ? task.Status == DevelopmentTaskStatus.InReview
            : task.Status is DevelopmentTaskStatus.Ready or DevelopmentTaskStatus.InProgress or DevelopmentTaskStatus.ChangesRequested;
        if (!allowed)
        {
            throw new DevelopmentInvalidTransitionException($"A {role} attempt cannot start while the task is {task.Status}.");
        }
    }

    private static void EnsureLegalTransition(DevelopmentTaskStatus source, DevelopmentTaskStatus target)
    {
        if (!LegalTaskTransitions.TryGetValue(source, out var targets) || !targets.Contains(target))
        {
            throw new DevelopmentInvalidTransitionException($"Development task transition {source} -> {target} is not legal.");
        }
    }

    private static void EnsureVersion(long actual, long expected, string resource)
    {
        if (actual != expected)
        {
            throw new DevelopmentConcurrencyException($"The {resource} version is stale (expected {expected}, current {actual}).");
        }
    }

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private static void ValidateArtifactCommand(DevelopmentAttachArtifactCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.ContentHash, "contentHash");
        if (command.SchemaVersion <= 0 || command.ByteCount < 0 || (command.ContentJson is null) == (command.ManagedReference is null))
        {
            throw new ArgumentException("An artifact requires a positive schema version and exactly one content representation.", nameof(command));
        }

        if (command.ManagedReference is not null
            && !string.Equals(command.ManagedReference, ManagedReference(command.ProjectId, command.ArtifactId), StringComparison.Ordinal))
        {
            throw new ArgumentException("A managed artifact reference must be the engine-generated opaque project/artifact key.", nameof(command));
        }
    }

    private DevelopmentArtifact BuildArtifact(DevelopmentAttachArtifactCommand command) =>
        new()
        {
            Id = command.ArtifactId,
            ProjectId = command.ProjectId,
            TaskId = command.TaskId,
            AttemptId = command.AttemptId,
            Kind = command.Kind,
            SchemaVersion = command.SchemaVersion,
            ContentJson = command.ContentJson?.ToArray(),
            ManagedReference = command.ManagedReference,
            ContentHash = command.ContentHash,
            ByteCount = command.ByteCount,
            CreatedAtUtc = Now(),
            BaseCommit = command.BaseCommit,
            SubjectHash = command.SubjectHash,
            ChangedFilesManifestHash = command.ChangedFilesManifestHash,
            InputArtifactIdsJson = command.InputArtifactIdsJson?.ToArray(),
            CommandProfileVersion = command.CommandProfileVersion,
            CommandProfileDigest = command.CommandProfileDigest,
            IsValid = true
        };

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static DevelopmentProjectSnapshot ProjectSnapshot(DevelopmentProject entity) =>
        new(entity.Id,
            Encoding.UTF8.GetString(entity.Objective),
            entity.SelectedFolderId,
            entity.RepositoryIdentityHash,
            entity.BaseBranch,
            entity.Status,
            entity.EgressPolicy,
            entity.CoderModelId,
            entity.ReviewerModelId,
            entity.MaxTokens,
            entity.MaxDurationSeconds,
            entity.ConfigurationVersion,
            entity.TrustedRepositoryAcknowledged,
            entity.TrustedRepositoryPolicyVersion,
            entity.TrustedRepositoryAcknowledgedAtUtc,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.Version,
            entity.CommandProfileJson);

    private static DevelopmentTaskSnapshot TaskSnapshot(DevelopmentTask entity) =>
        new(entity.Id,
            entity.ProjectId,
            Encoding.UTF8.GetString(entity.Title),
            Encoding.UTF8.GetString(entity.Requirements),
            Encoding.UTF8.GetString(entity.AcceptanceCriteriaJson),
            entity.Status,
            entity.CurrentReviewRound,
            entity.MaxReviewRounds,
            entity.BlockedReason,
            entity.BlockedAtUtc,
            entity.ApprovedSubjectHash,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.Version);

    private static DevelopmentArtifactSnapshot ArtifactSnapshot(DevelopmentArtifact entity) =>
        new(entity.Id,
            entity.ProjectId,
            entity.TaskId,
            entity.AttemptId,
            entity.Kind,
            entity.SchemaVersion,
            entity.ManagedReference,
            entity.ContentHash,
            entity.ByteCount,
            entity.CreatedAtUtc,
            entity.BaseCommit,
            entity.SubjectHash,
            entity.ChangedFilesManifestHash,
            entity.InputArtifactIdsJson,
            entity.CommandProfileVersion,
            entity.IsValid,
            entity.CommandProfileDigest);

    private static string ManagedReference(Guid projectId, Guid artifactId) =>
        string.Concat(projectId.ToString("N"), "/", artifactId.ToString("N"));

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The task an attempt belongs to and the project that owns that task.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct AttemptOwnership(Guid ProjectId, Guid TaskId);
}
