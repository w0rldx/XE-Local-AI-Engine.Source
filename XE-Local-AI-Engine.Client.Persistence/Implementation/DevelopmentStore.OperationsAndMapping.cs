namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
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
