namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for the training dataset module: definitions, datasets, samples, tool mocks and the durable
///     generation queue. Mirrors <see cref="IBenchmarkStore" />'s conventions — hand-bumped <c>Version</c> concurrency
///     tokens compared against a caller-supplied <c>expectedVersion</c>, explicit SQLite transactions around every
///     multi-row mutation, and explicit ordered child deletes (the node connection never enables foreign keys, so a
///     declared cascade does nothing).
/// </summary>
public interface ITrainingDatasetStore
{
    Task<TrainingDefinitionRecord> CreateDefinitionAsync(TrainingDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>Applies an edit and bumps BOTH the concurrency <c>Version</c> and the artifact <c>DefinitionVersion</c>.</summary>
    Task<TrainingDefinitionRecord> UpdateDefinitionAsync(Guid definitionId, long expectedVersion, TrainingDefinitionInput input, CancellationToken cancellationToken = default);

    Task<TrainingDefinitionRecord?> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingDefinitionRecord>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    Task DeleteDefinitionAsync(Guid definitionId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates the dataset row and its single queued work item in one transaction (freeze-before-enqueue). The
    ///     definition BODY is snapshotted onto the dataset in that same transaction, so a later edit cannot re-shape a
    ///     dataset that already claims an older <c>DefinitionVersion</c>.
    /// </summary>
    Task<TrainingDatasetRecord> CreateDatasetAndEnqueueAsync(TrainingDatasetEnqueueCommand command, CancellationToken cancellationToken = default);

    Task<TrainingDatasetRecord?> GetDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingDatasetRecord>> ListDatasetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Refused while a non-terminal generation work item still references the dataset.</summary>
    Task DeleteDatasetAsync(Guid datasetId, long expectedVersion, CancellationToken cancellationToken = default);

    Task<DatasetGenerationClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default);

    /// <summary>Terminalizes every interrupted <c>Running</c> work item as failed. Attempt is pinned to 1: never retried in place.</summary>
    Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes the dataset's work item and moves the dataset to its terminal status. Idempotent: a second call on
    ///     an already-terminal work item is a silent no-op. On success the content fingerprint is computed from the
    ///     persisted samples.
    /// </summary>
    Task<TrainingDatasetRecord> CompleteGenerationAsync(Guid datasetId, DatasetGenerationWorkStatus status, string? errorMessage, CancellationToken cancellationToken = default);

    /// <summary>Appends a generated sample, skipping (and counting) one whose source hash already exists in this dataset.</summary>
    Task<TrainingSampleAppendResult> AppendSampleAsync(TrainingSampleInput input, CancellationToken cancellationToken = default);

    /// <summary>Records a sample the pipeline refused to persist, so a rejection is never silently dropped.</summary>
    Task RecordRejectedSampleAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task<TrainingSamplePage> ListSamplesAsync(TrainingSampleQuery query, CancellationToken cancellationToken = default);

    /// <summary>Every sample in canonical (sequence) order — the export and fingerprint ordering.</summary>
    Task<IReadOnlyList<TrainingSampleRecord>> ListAllSamplesAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>Applies a review verb; bumps <c>TrainingDataset.Revision</c> and recomputes the content fingerprint.</summary>
    Task<TrainingSampleRecord> ReviewSampleAsync(TrainingSampleReviewCommand command, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> CreateMockAsync(ToolMockInput input, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> UpdateMockAsync(Guid mockId, long expectedVersion, ToolMockInput input, CancellationToken cancellationToken = default);

    Task<ToolMockRecord?> GetMockAsync(Guid mockId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolMockRecord>> ListMocksAsync(CancellationToken cancellationToken = default);

    /// <summary>Only <see cref="ToolMockVerificationState.Verified" /> AND enabled mocks — the engine has no fallthrough.</summary>
    Task<IReadOnlyList<ToolMockRecord>> ListUsableMocksAsync(string toolName, CancellationToken cancellationToken = default);

    Task DeleteMockAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> SetMockVerificationAsync(Guid mockId,
        long expectedVersion,
        ToolMockVerificationState state,
        ReadOnlyMemory<byte> verificationJson,
        CancellationToken cancellationToken = default);
}

public sealed class TrainingDefinitionInput
{
    public required string Name { get; init; }

    public required TrainingDatasetKind Kind { get; init; }

    public required ReadOnlyMemory<byte> DefinitionJson { get; init; }
}

public sealed class TrainingDefinitionRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required TrainingDatasetKind Kind { get; init; }

    public required ReadOnlyMemory<byte> DefinitionJson { get; init; }

    public required long DefinitionVersion { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class TrainingDatasetEnqueueCommand
{
    public required Guid DefinitionId { get; init; }

    public required long ExpectedDefinitionVersion { get; init; }

    public required string Name { get; init; }
}

/// <summary>
///     <see cref="DefinitionJson" /> is the definition body PINNED at creation — what generation and evaluation
///     must read. Null means the dataset predates pinning; it is never a cue to fall back to the live definition.
/// </summary>
public sealed record TrainingDatasetRecord
{
    public required Guid Id { get; init; }

    public required Guid DefinitionId { get; init; }

    public required long DefinitionVersion { get; init; }

    public required ReadOnlyMemory<byte>? DefinitionJson { get; init; }

    public required string Name { get; init; }

    public required TrainingDatasetStatus Status { get; init; }

    public required int Revision { get; init; }

    public required string? ContentFingerprint { get; init; }

    public required int TotalSampleCount { get; init; }

    public required int GoodSampleCount { get; init; }

    public required int BadSampleCount { get; init; }

    public required int RejectedSampleCount { get; init; }

    public required int DuplicateSampleCount { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required DatasetGenerationWorkStatus? WorkStatus { get; init; }

    public required string? WorkErrorMessage { get; init; }
}

public sealed record DatasetGenerationClaimedWork
{
    public required long QueueSequence { get; init; }

    public required Guid DatasetId { get; init; }

    public required long Version { get; init; }

    public required TrainingDatasetRecord Dataset { get; init; }
}

public sealed class TrainingSampleInput
{
    public required Guid DatasetId { get; init; }

    public required string Kind { get; init; }

    public required TrainingSampleLabel Label { get; init; }

    public required ReadOnlyMemory<byte> ContentJson { get; init; }

    public required ReadOnlyMemory<byte>? ValidationJson { get; init; }

    public required TrainingSampleProvenance Provenance { get; init; }

    public required string SourceHash { get; init; }
}

public sealed class TrainingSampleAppendResult
{
    public required TrainingSampleRecord? Sample { get; init; }

    public required bool Duplicate { get; init; }
}

public sealed class TrainingSampleRecord
{
    public required Guid Id { get; init; }

    public required Guid DatasetId { get; init; }

    public required int Sequence { get; init; }

    public required string Kind { get; init; }

    public required TrainingSampleLabel Label { get; init; }

    public required TrainingSampleReviewState ReviewState { get; init; }

    public required ReadOnlyMemory<byte> ContentJson { get; init; }

    public required ReadOnlyMemory<byte>? ValidationJson { get; init; }

    public required TrainingSampleProvenance Provenance { get; init; }

    public required string SourceHash { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class TrainingSampleQuery
{
    public required Guid DatasetId { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public TrainingSampleLabel? Label { get; init; }

    public TrainingSampleReviewState? ReviewState { get; init; }

    public string? Kind { get; init; }
}

public sealed class TrainingSamplePage
{
    public required IReadOnlyList<TrainingSampleRecord> Items { get; init; }

    public required int TotalCount { get; init; }
}

/// <summary>Review verbs. <see cref="Label" /> is only honored by <see cref="TrainingSampleReviewVerb.Relabel" />.</summary>
public sealed class TrainingSampleReviewCommand
{
    public required Guid SampleId { get; init; }

    public required TrainingSampleReviewVerb Verb { get; init; }

    public TrainingSampleLabel? Label { get; init; }
}

public enum TrainingSampleReviewVerb
{
    Approve,
    Reject,
    Relabel
}

public sealed class ToolMockInput
{
    public required string ToolName { get; init; }

    public required ReadOnlyMemory<byte> MockJson { get; init; }

    public required bool Enabled { get; init; }
}

public sealed class ToolMockRecord
{
    public required Guid Id { get; init; }

    public required string ToolName { get; init; }

    public required ReadOnlyMemory<byte> MockJson { get; init; }

    public required ReadOnlyMemory<byte>? VerificationJson { get; init; }

    public required ToolMockVerificationState VerificationState { get; init; }

    public required bool Enabled { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public abstract class TrainingStoreException : InvalidOperationException
{
    protected TrainingStoreException(string message)
        : base(message)
    {
    }

    protected TrainingStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TrainingNotFoundException : TrainingStoreException
{
    public TrainingNotFoundException(string message) : base(message)
    {
    }
}

public sealed class TrainingValidationException : TrainingStoreException
{
    public TrainingValidationException(string message)
        : base(message)
    {
    }

    public TrainingValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TrainingConflictException : TrainingStoreException
{
    public TrainingConflictException(string code)
        : base(code) =>
        Code = code;

    public TrainingConflictException(string code, Exception innerException)
        : base(code, innerException) =>
        Code = code;

    public string Code { get; }
}
