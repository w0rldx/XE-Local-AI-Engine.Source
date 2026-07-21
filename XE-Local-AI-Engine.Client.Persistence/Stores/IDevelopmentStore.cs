namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public static class DevelopmentOperationPhases
{
    public const string Completed = "Completed";
    public const string ApplyStarted = "ApplyStarted";
    public const string ApplyCompleted = "ApplyCompleted";
    public const string ApplyBlocked = "ApplyBlocked";
}

public sealed record DevelopmentCreateProjectCommand(
    Guid ProjectId,
    Guid TaskId,
    Guid OperationId,
    string Objective,
    string RepositoryIdentityHash,
    string BaseBranch,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentEgressPolicy EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
    string? CoderModelId = null,
    string? ReviewerModelId = null,
    int MaxReviewRounds = 3,
    int ConfigurationVersion = 1,
    bool TrustedRepositoryAcknowledged = false,
    int? TrustedRepositoryPolicyVersion = null,
    long? TrustedRepositoryAcknowledgedAtUtc = null,
    int? MaxTokens = null,
    int? MaxDurationSeconds = null);

public sealed record DevelopmentStartAttemptCommand(
    Guid TaskId,
    Guid AttemptId,
    Guid OperationId,
    DevelopmentAttemptRole Role,
    string ModelId,
    string Provider,
    long ExpectedTaskVersion,
    Guid? PredecessorAttemptId = null);

public sealed record DevelopmentTerminalizeAttemptCommand(
    Guid AttemptId,
    Guid OperationId,
    DevelopmentAttemptStatus Status,
    long ExpectedAttemptVersion,
    string? TerminalReason = null,
    long? InputTokens = null,
    long? OutputTokens = null);

public sealed record DevelopmentTransitionTaskCommand(
    Guid TaskId,
    Guid OperationId,
    DevelopmentTaskStatus TargetStatus,
    long ExpectedTaskVersion,
    string? Reason = null,
    string? ApprovedSubjectHash = null);

public sealed record DevelopmentAttachArtifactCommand(
    Guid ArtifactId,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    Guid OperationId,
    DevelopmentArtifactKind Kind,
    int SchemaVersion,
    string ContentHash,
    long ByteCount,
    ReadOnlyMemory<byte>? ContentJson = null,
    string? ManagedReference = null,
    string? BaseCommit = null,
    string? SubjectHash = null,
    string? ChangedFilesManifestHash = null,
    ReadOnlyMemory<byte>? InputArtifactIdsJson = null,
    string? CommandProfileVersion = null);

public sealed record DevelopmentApprovedApplySubject(
    Guid ProjectId,
    Guid TaskId,
    long ExpectedTaskVersion,
    string BaseCommit,
    string PatchHash,
    string ManifestHash,
    string ExpectedResultHash,
    string PatchArtifactReference,
    string ManifestArtifactReference);

public sealed record DevelopmentOperationResult(
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    Guid? ArtifactId,
    Guid OperationId,
    string Phase,
    string Outcome,
    string Status,
    long Version,
    long Sequence);

public sealed record DevelopmentEventSnapshot(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    long Sequence,
    string EventType,
    long OccurredAtUtc,
    Guid? OperationId,
    string? OperationPhase,
    string? Outcome);

public sealed record DevelopmentExecutionSnapshot(
    Guid ProjectId,
    Guid TaskId,
    Guid AttemptId,
    string RepositoryIdentityHash,
    string BaseBranch,
    DevelopmentEgressPolicy EgressPolicy,
    int ConfigurationVersion,
    bool TrustedRepositoryAcknowledged,
    int? TrustedRepositoryPolicyVersion,
    long? TrustedRepositoryAcknowledgedAtUtc,
    int? MaxTokens,
    int? MaxDurationSeconds,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentTaskStatus TaskStatus,
    long TaskVersion,
    DevelopmentAttemptRole AttemptRole,
    DevelopmentAttemptStatus AttemptStatus,
    string ModelId,
    string Provider,
    long AttemptVersion);

public interface IDevelopmentStore
{
    Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default);
    Task<int> ReconcileRunningAttemptsAsync(string sanitizedReason, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult?> FindOperationAsync(Guid projectId, Guid operationId, string phase, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> RecordApplyStartedAsync(Guid operationId, DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> CompleteApplyAsync(Guid operationId, DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> BlockApplyAsync(Guid operationId, DevelopmentApprovedApplySubject subject, string sanitizedReason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentExecutionSnapshot> GetExecutionSnapshotAsync(Guid attemptId, CancellationToken cancellationToken = default);
}

public sealed class DevelopmentConcurrencyException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public sealed class DevelopmentInvalidTransitionException(string message) : InvalidOperationException(message);
