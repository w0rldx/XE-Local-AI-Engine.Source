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

public sealed record TrainingDefinitionInput(string Name, TrainingDatasetKind Kind, ReadOnlyMemory<byte> DefinitionJson);

public sealed record TrainingDefinitionRecord(
    Guid Id,
    string Name,
    TrainingDatasetKind Kind,
    ReadOnlyMemory<byte> DefinitionJson,
    long DefinitionVersion,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record TrainingDatasetEnqueueCommand(Guid DefinitionId, long ExpectedDefinitionVersion, string Name);

/// <summary>
///     <paramref name="DefinitionJson" /> is the definition body PINNED at creation — what generation and evaluation
///     must read. Null means the dataset predates pinning; it is never a cue to fall back to the live definition.
/// </summary>
public sealed record TrainingDatasetRecord(
    Guid Id,
    Guid DefinitionId,
    long DefinitionVersion,
    ReadOnlyMemory<byte>? DefinitionJson,
    string Name,
    TrainingDatasetStatus Status,
    int Revision,
    string? ContentFingerprint,
    int TotalSampleCount,
    int GoodSampleCount,
    int BadSampleCount,
    int RejectedSampleCount,
    int DuplicateSampleCount,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    DatasetGenerationWorkStatus? WorkStatus,
    string? WorkErrorMessage);

public sealed record DatasetGenerationClaimedWork(long QueueSequence, Guid DatasetId, long Version, TrainingDatasetRecord Dataset);

public sealed record TrainingSampleInput(
    Guid DatasetId,
    string Kind,
    TrainingSampleLabel Label,
    ReadOnlyMemory<byte> ContentJson,
    ReadOnlyMemory<byte>? ValidationJson,
    TrainingSampleProvenance Provenance,
    string SourceHash);

public sealed record TrainingSampleAppendResult(TrainingSampleRecord? Sample, bool Duplicate);

public sealed record TrainingSampleRecord(
    Guid Id,
    Guid DatasetId,
    int Sequence,
    string Kind,
    TrainingSampleLabel Label,
    TrainingSampleReviewState ReviewState,
    ReadOnlyMemory<byte> ContentJson,
    ReadOnlyMemory<byte>? ValidationJson,
    TrainingSampleProvenance Provenance,
    string SourceHash,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record TrainingSampleQuery(
    Guid DatasetId,
    int Page,
    int PageSize,
    TrainingSampleLabel? Label = null,
    TrainingSampleReviewState? ReviewState = null,
    string? Kind = null);

public sealed record TrainingSamplePage(IReadOnlyList<TrainingSampleRecord> Items, int TotalCount);

/// <summary>Review verbs. <paramref name="Label" /> is only honored by <see cref="TrainingSampleReviewVerb.Relabel" />.</summary>
public sealed record TrainingSampleReviewCommand(Guid SampleId, TrainingSampleReviewVerb Verb, TrainingSampleLabel? Label = null);

public enum TrainingSampleReviewVerb
{
    Approve,
    Reject,
    Relabel
}

public sealed record ToolMockInput(string ToolName, ReadOnlyMemory<byte> MockJson, bool Enabled);

public sealed record ToolMockRecord(
    Guid Id,
    string ToolName,
    ReadOnlyMemory<byte> MockJson,
    ReadOnlyMemory<byte>? VerificationJson,
    ToolMockVerificationState VerificationState,
    bool Enabled,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public abstract class TrainingStoreException(string message) : InvalidOperationException(message);

public sealed class TrainingNotFoundException(string message) : TrainingStoreException(message);

public sealed class TrainingValidationException(string message) : TrainingStoreException(message);

public sealed class TrainingConflictException(string code) : TrainingStoreException(code)
{
    public string Code { get; } = code;
}
