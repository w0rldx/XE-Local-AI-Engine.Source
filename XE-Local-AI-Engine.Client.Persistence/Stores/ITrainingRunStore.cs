namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for training runs, their durable queue and their staged artifacts.
/// </summary>
/// <remarks>
///     Same conventions as <see cref="ITrainingDatasetStore" /> — hand-bumped <c>Version</c> tokens against a caller-supplied
///     <c>expectedVersion</c>, explicit SQLite transactions, explicit ordered child deletes, the shared
///     <see cref="TrainingStoreException" /> hierarchy. <c>Version</c> guards operator-visible transitions (status, deletes,
///     artifact state); <see cref="UpdateProgressAsync" />, <see cref="AppendLogTailAsync" /> and <see cref="SetLaunchReceiptAsync" />
///     leave it alone: they fire many times per run from the run's single executor and would invalidate its expected version.
/// </remarks>
public interface ITrainingRunStore
{
    /// <summary>
    ///     Creates the run and its single queued work item in one transaction.
    /// </summary>
    /// <remarks>
    ///     The dataset's content fingerprint and revision are read inside that transaction and copied onto the run —
    ///     that copy IS the freeze, so a concurrent sample edit cannot slip between the read and the insert. Refuses a
    ///     dataset that is not <see cref="TrainingDatasetStatus.Ready" />, a base artifact that is not
    ///     <see cref="TrainingBaseArtifactStatus.Ready" />, and a command with no license confirmation.
    /// </remarks>
    Task<TrainingRunRecord> CreateAndEnqueueAsync(TrainingRunEnqueueCommand command, CancellationToken cancellationToken = default);

    Task<TrainingRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<TrainingRunPage> ListAsync(TrainingRunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Claims the lowest-sequence queued work item of either kind by compare-and-swap. Null when the queue is empty.</summary>
    Task<TrainingWorkClaim?> ClaimNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same claim, scoped to one work kind.
    /// </summary>
    /// <remarks>
    ///     The consumer acquires the exclusivity a kind needs BEFORE it claims, so it has to be able to say "claim
    ///     only what I am holding the right locks for": an unscoped claim that returned the other kind would be
    ///     running with the wrong locks and could not be handed back, because attempt is pinned to 1 and there is no
    ///     retry.
    /// </remarks>
    Task<TrainingWorkClaim?> ClaimNextAsync(TrainingWorkKind onlyKind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The kind of the work item a claim would take next, without taking it. Null when the queue is empty.
    /// </summary>
    /// <remarks>
    ///     The consumer needs it to decide which exclusivity to acquire. The head cannot be overtaken — queue
    ///     sequences only ever increase and there is one consumer — and a head that terminalizes between the peek and
    ///     the claim only makes the scoped claim return null.
    /// </remarks>
    Task<TrainingWorkKind?> PeekNextKindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes every interrupted <c>Running</c> work item as failed and fails the non-terminal runs behind
    ///     them. Attempt is pinned to 1: never retried in place. Idempotent — a second call finds nothing to recover.
    ///     Returns the target ids it moved.
    /// </summary>
    /// <remarks>
    ///     Deliberately leaves <see cref="TrainingRunRecord.LaunchReceiptJson" /> alone. A receipt is the ONLY handle
    ///     the host has on a trainer that outlived it, so clearing one here — before the reaper has proved the process
    ///     is dead — turns a live orphan into an unidentifiable one holding VRAM forever. The reaper clears each
    ///     receipt itself (<see cref="SetLaunchReceiptAsync" /> with null) once it has killed or ruled out the process.
    /// </remarks>
    Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every run still carrying a launch receipt, unpaged. The startup reaper cannot read receipts off a page of
    ///     <see cref="ListAsync" />: a live trainer whose run is older than one page of newer runs would fall outside
    ///     it, and the orphan would never be inspected.
    /// </summary>
    Task<IReadOnlyList<TrainingRunLaunchReceipt>> ListLaunchReceiptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves the run along its non-terminal progression (<c>Preparing</c>, <c>Training</c>, <c>Exporting</c>,
    ///     <c>Smoke</c>) under the expected version. A terminal target status is rejected here — those go through
    ///     <see cref="CompleteRunAsync" /> so the work item is terminalized in the same transaction.
    /// </summary>
    Task<TrainingRunRecord> TransitionAsync(Guid runId, long expectedVersion, TrainingRunStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes the run's work item and moves the run to its terminal status in one transaction. Idempotent: a
    ///     second call on an already-terminal work item is a silent no-op, so a startup retrace cannot double-transition.
    /// </summary>
    Task<TrainingRunRecord> CompleteRunAsync(Guid runId, TrainingWorkStatus status, string? errorMessage, CancellationToken cancellationToken = default);

    /// <summary>Replaces the latest progress snapshot. Does not bump <c>Version</c> — see the interface remarks.</summary>
    Task UpdateProgressAsync(Guid runId, ReadOnlyMemory<byte> progressJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends trainer output to the log tail, keeping only the last <c>MaxLogTailLength</c> characters. Bounded in
    ///     the store because the column is ciphertext at rest and SQLite cannot see its plaintext length.
    /// </summary>
    Task AppendLogTailAsync(Guid runId, string chunk, CancellationToken cancellationToken = default);

    /// <summary>Records (or clears, with null) what the host needs to identify and reap the trainer process.</summary>
    Task SetLaunchReceiptAsync(Guid runId, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the run, its artifacts and its work item in that order. Refused while the work item is still
    ///     non-terminal, and refused while any artifact has been promoted to the registry.
    /// </summary>
    Task DeleteAsync(Guid runId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Records a freshly staged artifact under the run's directory, smoke state <c>Pending</c>.</summary>
    Task<TrainingArtifactRecord> CreateArtifactAsync(TrainingArtifactInput input, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingArtifactRecord>> ListArtifactsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<TrainingArtifactRecord?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>Records the digest and size once the export step has finished writing the staged bytes.</summary>
    Task<TrainingArtifactRecord> SetArtifactDigestAsync(Guid artifactId,
        long expectedVersion,
        string sha256,
        long sizeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies the smoke outcome. <see cref="TrainingArtifactSmokeState.Pending" /> is rejected — smoke state only
    ///     ever moves forward into a decided value, and <c>Failed</c>/<c>Skipped</c> require a reason.
    /// </summary>
    Task<TrainingArtifactRecord> SetArtifactSmokeStateAsync(Guid artifactId,
        long expectedVersion,
        TrainingArtifactSmokeState state,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Marks the artifact promoted under a registry name, or clears the promotion with null once the registry
    ///     entry has been removed — without that, a promoted artifact and the run behind it could never be deleted.
    /// </summary>
    /// <remarks>
    ///     Promoting is refused while smoke is still <see cref="TrainingArtifactSmokeState.Pending" /> or
    ///     <see cref="TrainingArtifactSmokeState.Failed" />: staged is inert, and only a passed (or explicitly
    ///     skipped) smoke lets an artifact out.
    /// </remarks>
    Task<TrainingArtifactRecord> SetArtifactCommittedNameAsync(Guid artifactId,
        long expectedVersion,
        string? committedModelName,
        CancellationToken cancellationToken = default);

    Task<TrainingArtifactRecord> SetArtifactQualityDecisionAsync(Guid artifactId,
        long expectedVersion,
        Guid comparisonId,
        ReadOnlyMemory<byte> decisionJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Atomically turns an unpromoted quality-decided artifact into an audit tombstone. The decision history is
    ///     retained, while its live comparison reference is released so the comparison can be deleted independently.
    /// </summary>
    Task<TrainingArtifactRecord> DiscardArtifactQualityAsync(Guid artifactId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotently records that a tombstone's staged bytes are absent without rewriting its audit fields.</summary>
    Task<TrainingArtifactRecord> CompleteArtifactDiscardCleanupAsync(Guid artifactId,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a staged artifact row. Refused once it has been promoted.</summary>
    Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Everything the freeze needs. <see cref="ExpectedDatasetVersion" /> pins the dataset the caller inspected:
///     any sample mutation bumps it, so a stale confirmation dialog cannot start a run against a dataset that moved.
/// </summary>
public sealed record TrainingRunEnqueueCommand
{
    public required Guid DatasetId { get; init; }

    public required long ExpectedDatasetVersion { get; init; }

    public required Guid BaseArtifactId { get; init; }

    public required ReadOnlyMemory<byte> FreezeJson { get; init; }

    public required ReadOnlyMemory<byte> OptionsJson { get; init; }

    public required ReadOnlyMemory<byte> LicenseConfirmationJson { get; init; }

    public string? LinkedInstalledModelName { get; init; }

    public string? LinkedModelContentFingerprint { get; init; }
}

/// <summary>
///     A run as the application layer sees it. The encrypted documents are carried as <see cref="ReadOnlyMemory{T}" />
///     so the record cannot hand a caller a mutable reference to the decrypted column contents; the log tail is
///     decoded to text because the store owns its encoding.
/// </summary>
public sealed record TrainingRunRecord
{
    public required Guid Id { get; init; }

    public required Guid DatasetId { get; init; }

    public required string DatasetContentFingerprint { get; init; }

    public required int DatasetRevision { get; init; }

    public required ReadOnlyMemory<byte> FreezeJson { get; init; }

    public required Guid BaseArtifactId { get; init; }

    public required string? LinkedInstalledModelName { get; init; }

    public required string? LinkedModelContentFingerprint { get; init; }

    public required ReadOnlyMemory<byte> OptionsJson { get; init; }

    public required ReadOnlyMemory<byte>? LicenseConfirmationJson { get; init; }

    public required TrainingRunStatus Status { get; init; }

    public required ReadOnlyMemory<byte>? ProgressJson { get; init; }

    public required string? LogTail { get; init; }

    public required ReadOnlyMemory<byte>? LaunchReceiptJson { get; init; }

    public required string? ErrorMessage { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required TrainingWorkStatus? WorkStatus { get; init; }

    public required string? WorkErrorMessage { get; init; }
}

/// <summary>One run's recorded launch receipt. Carries the run id because the reaper clears the receipt it acted on.</summary>
public sealed class TrainingRunLaunchReceipt
{
    public required Guid RunId { get; init; }

    public required ReadOnlyMemory<byte> LaunchReceiptJson { get; init; }
}

public sealed class TrainingRunQuery
{
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public Guid? DatasetId { get; init; }

    public TrainingRunStatus? Status { get; init; }
}

public sealed class TrainingRunPage
{
    public required IReadOnlyList<TrainingRunRecord> Items { get; init; }

    public required int TotalCount { get; init; }
}

/// <summary>
///     A claimed work item. <see cref="Run" /> is populated only for <see cref="TrainingWorkKind.TrainingRun" /> —
///     an evaluation target lives in another table this store does not own.
/// </summary>
public sealed class TrainingWorkClaim
{
    public required long QueueSequence { get; init; }

    public required TrainingWorkKind Kind { get; init; }

    public required Guid TargetId { get; init; }

    public required long Version { get; init; }

    public required TrainingRunRecord? Run { get; init; }
}

public sealed record TrainingArtifactInput
{
    public required Guid RunId { get; init; }

    public required TrainingArtifactKind Kind { get; init; }

    public required string Path { get; init; }
}

public sealed record TrainingArtifactRecord
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required TrainingArtifactKind Kind { get; init; }

    public required string Path { get; init; }

    public required string? Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required TrainingArtifactSmokeState SmokeState { get; init; }

    public required string? SmokeReason { get; init; }

    public required string? CommittedModelName { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public Guid? QualityComparisonId { get; init; }

    public ReadOnlyMemory<byte>? QualityDecisionJson { get; init; }

    public long? DiscardedAtUtc { get; init; }

    public string? DiscardReason { get; init; }

    public bool DiscardCleanupPending { get; init; }
}
