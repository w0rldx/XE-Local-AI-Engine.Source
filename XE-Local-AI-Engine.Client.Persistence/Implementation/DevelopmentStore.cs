namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IDevelopmentStore
{
    private const string StartupOperationPhase = "StartupInterrupted";

    private static readonly IReadOnlyDictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>> LegalTaskTransitions =
        new Dictionary<DevelopmentTaskStatus, HashSet<DevelopmentTaskStatus>>
        {
            [DevelopmentTaskStatus.Planned] = [DevelopmentTaskStatus.Ready, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Ready] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.InProgress] = [DevelopmentTaskStatus.Validation, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.Validation] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.InReview, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.InReview] = [DevelopmentTaskStatus.ChangesRequested, DevelopmentTaskStatus.AwaitingApply, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.ChangesRequested] = [DevelopmentTaskStatus.InProgress, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled],
            [DevelopmentTaskStatus.AwaitingApply] = [DevelopmentTaskStatus.Completed, DevelopmentTaskStatus.Blocked, DevelopmentTaskStatus.Cancelled]
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
                    RepositoryIdentityHash = command.RepositoryIdentityHash,
                    BaseBranch = command.BaseBranch,
                    Status = DevelopmentProjectStatus.Active,
                    EgressPolicy = command.EgressPolicy,
                    CoderModelId = command.CoderModelId,
                    ReviewerModelId = command.ReviewerModelId,
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
                    Version = 1
                };
                _dbContext.DevelopmentAttempts.Add(attempt);

                if (command.Role == DevelopmentAttemptRole.Coder && task.Status != DevelopmentTaskStatus.InProgress)
                {
                    task.Status = DevelopmentTaskStatus.InProgress;
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
                    detailJson: command.Reason is null ? null : Utf8(JsonSerializer.Serialize(new { reason = command.Reason })),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.ContentHash, "contentHash");
        if (command.SchemaVersion <= 0 || command.ByteCount < 0 || (command.ContentJson is null) == (command.ManagedReference is null))
        {
            throw new ArgumentException("An artifact requires a positive schema version and exactly one content representation.", nameof(command));
        }

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

                var artifact = new DevelopmentArtifact
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
                    IsValid = true
                };
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
                    Utf8(JsonSerializer.Serialize(new { reason = sanitizedReason })),
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

    private async Task<(Guid ProjectId, Guid TaskId)> OwnershipForAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var taskId = await _dbContext.DevelopmentAttempts.AsNoTracking()
                                     .Where(entity => entity.Id == attemptId)
                                     .Select(entity => entity.TaskId)
                                     .SingleAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return (await ProjectIdForTaskAsync(taskId, cancellationToken).ConfigureAwait(false), taskId);
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

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
}
