namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for training runs, their durable queue and their staged artifacts. Same conventions as
///     <see cref="ITrainingDatasetStore" /> — hand-bumped <c>Version</c> concurrency tokens compared against a
///     caller-supplied <c>expectedVersion</c>, explicit SQLite transactions around every multi-row mutation, explicit
///     ordered child deletes, and the shared <see cref="TrainingStoreException" /> hierarchy.
/// </summary>
/// <remarks>
///     <para>
///         <strong>What <c>Version</c> guards.</strong> It guards operator-visible transitions — status changes,
///         deletes, artifact state. The three telemetry writers (<see cref="UpdateProgressAsync" />,
///         <see cref="AppendLogTailAsync" />, <see cref="SetLaunchReceiptAsync" />) deliberately leave it alone: they
///         fire many times per run from the single executor that owns the run, and bumping the token there would
///         invalidate the caller's expected version between every progress tick.
///     </para>
/// </remarks>
public interface ITrainingRunStore
{
    /// <summary>
    ///     Creates the run and its single queued work item in one transaction. The dataset's content fingerprint and
    ///     revision are read inside that transaction and copied onto the run — that copy IS the freeze, so a concurrent
    ///     sample edit cannot slip between the read and the insert. Refuses a dataset that is not
    ///     <see cref="TrainingDatasetStatus.Ready" />, a base artifact that is not
    ///     <see cref="TrainingBaseArtifactStatus.Ready" />, and a command with no license confirmation.
    /// </summary>
    Task<TrainingRunRecord> CreateAndEnqueueAsync(TrainingRunEnqueueCommand command, CancellationToken cancellationToken = default);

    Task<TrainingRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<TrainingRunPage> ListAsync(TrainingRunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Claims the lowest-sequence queued work item of either kind by compare-and-swap. Null when the queue is empty.</summary>
    Task<TrainingWorkClaim?> ClaimNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same claim, scoped to one work kind. The consumer acquires the exclusivity a kind needs BEFORE it
    ///     claims, so it has to be able to say "claim only what I am holding the right locks for": an unscoped claim
    ///     that returned the other kind would be running with the wrong locks and could not be handed back, because
    ///     attempt is pinned to 1 and there is no retry.
    /// </summary>
    Task<TrainingWorkClaim?> ClaimNextAsync(TrainingWorkKind onlyKind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The kind of the work item a claim would take next, without taking it. The consumer needs it to decide which
    ///     exclusivity to acquire; the head cannot be overtaken because queue sequences only ever increase and there is
    ///     one consumer, and a head that terminalizes between the peek and the claim only makes the scoped claim return
    ///     null. Null when the queue is empty.
    /// </summary>
    Task<TrainingWorkKind?> PeekNextKindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes every interrupted <c>Running</c> work item as failed and fails the non-terminal runs behind
    ///     them. Attempt is pinned to 1: never retried in place. Idempotent — a second call finds nothing to recover.
    ///     Returns the target ids it moved.
    /// </summary>
    Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

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
    ///     Marks the artifact promoted under a registry name, or clears the promotion with null once the registry entry
    ///     has been removed — without that, a promoted artifact and the run behind it could never be deleted. Promoting
    ///     is refused while smoke is still <see cref="TrainingArtifactSmokeState.Pending" /> or
    ///     <see cref="TrainingArtifactSmokeState.Failed" />: staged is inert, and only a passed (or explicitly skipped)
    ///     smoke lets an artifact out.
    /// </summary>
    Task<TrainingArtifactRecord> SetArtifactCommittedNameAsync(Guid artifactId,
        long expectedVersion,
        string? committedModelName,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a staged artifact row. Refused once it has been promoted.</summary>
    Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Everything the freeze needs. <paramref name="ExpectedDatasetVersion" /> pins the dataset the caller inspected:
///     any sample mutation bumps it, so a stale confirmation dialog cannot start a run against a dataset that moved.
/// </summary>
public sealed record TrainingRunEnqueueCommand(
    Guid DatasetId,
    long ExpectedDatasetVersion,
    Guid BaseArtifactId,
    ReadOnlyMemory<byte> FreezeJson,
    ReadOnlyMemory<byte> OptionsJson,
    ReadOnlyMemory<byte> LicenseConfirmationJson,
    string? LinkedInstalledModelName = null,
    string? LinkedModelContentFingerprint = null);

/// <summary>
///     A run as the application layer sees it. The encrypted documents are carried as <see cref="ReadOnlyMemory{T}" />
///     so the record cannot hand a caller a mutable reference to the decrypted column contents; the log tail is
///     decoded to text because the store owns its encoding.
/// </summary>
public sealed record TrainingRunRecord(
    Guid Id,
    Guid DatasetId,
    string DatasetContentFingerprint,
    int DatasetRevision,
    ReadOnlyMemory<byte> FreezeJson,
    Guid BaseArtifactId,
    string? LinkedInstalledModelName,
    string? LinkedModelContentFingerprint,
    ReadOnlyMemory<byte> OptionsJson,
    ReadOnlyMemory<byte>? LicenseConfirmationJson,
    TrainingRunStatus Status,
    ReadOnlyMemory<byte>? ProgressJson,
    string? LogTail,
    ReadOnlyMemory<byte>? LaunchReceiptJson,
    string? ErrorMessage,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    TrainingWorkStatus? WorkStatus,
    string? WorkErrorMessage);

public sealed record TrainingRunQuery(int Page, int PageSize, Guid? DatasetId = null, TrainingRunStatus? Status = null);

public sealed record TrainingRunPage(IReadOnlyList<TrainingRunRecord> Items, int TotalCount);

/// <summary>
///     A claimed work item. <paramref name="Run" /> is populated only for <see cref="TrainingWorkKind.TrainingRun" /> —
///     an evaluation target lives in another table this store does not own.
/// </summary>
public sealed record TrainingWorkClaim(long QueueSequence, TrainingWorkKind Kind, Guid TargetId, long Version, TrainingRunRecord? Run);

public sealed record TrainingArtifactInput(Guid RunId, TrainingArtifactKind Kind, string Path);

public sealed record TrainingArtifactRecord(
    Guid Id,
    Guid RunId,
    TrainingArtifactKind Kind,
    string Path,
    string? Sha256,
    long SizeBytes,
    TrainingArtifactSmokeState SmokeState,
    string? SmokeReason,
    string? CommittedModelName,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);
