namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IBenchmarkStore
{
    Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input, CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord> UpdateProjectAsync(Guid projectId, long expectedVersion, BenchmarkProjectInput input, CancellationToken cancellationToken = default);
    Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkRunRecord>> ListRunsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkPrimaryFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);

    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkPrimaryCancelledAsync(runId, expectedRunVersion, cancellationToken);

    Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeCancelledAsync(runId, expectedRunVersion, cancellationToken);

    /// <summary>
    ///     Records the primary phase's durable launch evidence: an insert-if-null write of the receipt/environment
    ///     columns, keyed by the immutable work item rather than by the run's mutable version. It never overwrites an
    ///     existing block and never changes any status, so it is safe to call before inference and again while
    ///     terminalizing. Returns <see langword="true" /> when this call wrote the block.
    /// </summary>
    /// <param name="workItemId">The claimed work item's queue sequence.</param>
    /// <param name="claimedWorkVersion">
    ///     The work-item version the caller claimed. The write is accepted while that work item is still
    ///     <c>Running</c> at exactly that version, or already <c>Cancelled</c> at its successor version (terminalizing
    ///     a work item bumps the version by exactly one) — the cancel-first ordering. Anything else is refused.
    /// </param>
    Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MarkPrimaryLaunchReadyAsync" />
    Task<bool> MarkJudgeLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int score, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        _ = await RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
}

public sealed record BenchmarkProjectInput(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    string? JudgeModelName,
    int? JudgeContextTokens,
    int JudgePromptVersion = 1,
    int JudgeOutputSchemaVersion = 1);

public sealed record BenchmarkStartRunCommand(
    Guid RunId,
    Guid ProjectId,
    long ExpectedProjectVersion,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    bool JudgeEnabled,
    IBenchmarkFreezeCommitGuard? FreezeCommitGuard = null,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    BenchmarkRunLaunchIntent? JudgeLaunchIntent = null);

/// <summary>
///     Application-owned dependency guard executed by <see cref="IBenchmarkStore.StartRunAsync" /> inside the same
///     transaction that verifies the project version and inserts the run/work rows. Returning <see langword="false" />
///     aborts the transaction with <c>FreezeDependencyChanged</c>.
/// </summary>
public interface IBenchmarkFreezeCommitGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public sealed record BenchmarkPrimarySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> OutputPartsJson,
    long LastStreamSequence,
    int EffectiveContextTokens,
    long DurationMs,
    int? TotalTokens,
    double? TokensPerSecond);

public sealed record BenchmarkJudgeSuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> JudgeResultJson,
    long LastStreamSequence = 0);

public sealed record BenchmarkProjectRecord(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    string? JudgeModelName,
    int? JudgeContextTokens,
    int JudgePromptVersion,
    int JudgeOutputSchemaVersion,
    bool IsFrozen,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record BenchmarkRunRecord(
    Guid Id,
    Guid ProjectId,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    BenchmarkPrimaryStatus PrimaryStatus,
    int? EffectiveContextTokens,
    long? DurationMs,
    int? TotalTokens,
    double? TokensPerSecond,
    ReadOnlyMemory<byte>? OutputPartsJson,
    long LastStreamSequence,
    int? UserScore,
    BenchmarkJudgeStatus JudgeStatus,
    ReadOnlyMemory<byte>? JudgeResultJson,
    string? PrimaryErrorMessage,
    string? JudgeErrorMessage,
    long Version,
    long CreatedAtUtc,
    long? StartedAtUtc,
    long? PrimaryCompletedAtUtc,
    long? JudgeStartedAtUtc,
    long? JudgeCompletedAtUtc,
    long UpdatedAtUtc,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    BenchmarkRunLaunchIntent? JudgeLaunchIntent = null,
    BenchmarkRunLaunchEvidence? PrimaryLaunchEvidence = null,
    BenchmarkRunLaunchEvidence? JudgeLaunchEvidence = null);

/// <summary>
///     What freeze decided one phase of a run would launch with, before anything was spawned. Compared against the
///     evidence the launch itself recorded; the two differing is a fact the UI shows, not an error.
/// </summary>
/// <param name="KvCacheTypeSource"><c>explicit</c> when the run asked for this type, <c>auto</c> when freeze picked it.</param>
/// <param name="KvAutoReason">Why Auto did not pick the quantized type, or <see langword="null" /> when it did.</param>
public sealed record BenchmarkRunLaunchIntent(
    string Variant,
    string KvCacheType,
    string KvCacheTypeSource,
    string? KvAutoReason,
    string FlashAttentionMode,
    string IntendedLaunchIdentity,
    string? IntendedExecutableSha256);

/// <summary>
///     The durable launch evidence recorded for one phase. <see cref="ReceiptJson" /> is null when the spawn never
///     reached readiness — the environment capture is still recorded, because a failed launch is exactly when the
///     host facts matter.
/// </summary>
public sealed record BenchmarkRunLaunchEvidence(
    ReadOnlyMemory<byte>? ReceiptJson,
    ReadOnlyMemory<byte>? EnvironmentFactsJson,
    string? ReceiptHash,
    string? EnvironmentFactsHash,
    string? EffectiveLaunchIdentity,
    string? EffectiveBackend,
    int? PlacementOffloaded,
    int? PlacementTotal,
    string? ExecutableSha256,
    bool? HasAuxAssets,
    string? KvCacheTypeSource);

/// <summary>
///     Everything a run's durable launch-ready checkpoint records about what actually launched: the provider-owned
///     receipt and the pre-launch environment facts (both canonical JSON, encrypted at rest by the store), their
///     hashes, and the flat columns the list/compare views read without decrypting a payload.
/// </summary>
/// <remarks>
///     Deliberately strings, integers and flags only — the list view reads every column here without decrypting or
///     parsing the receipt payload. The receipt is assembled in the llama-server provider and serialized
///     before it reaches the store, so persisting it never drags a provider type through the store contract. Every
///     receipt-derived member is null together when the spawn failed before readiness.
/// </remarks>
public sealed record BenchmarkLaunchReceiptCommand(
    string? ReceiptJson,
    string EnvironmentFactsJson,
    string EnvironmentFactsHash,
    string? ReceiptHash,
    string? EffectiveLaunchIdentity,
    string? EffectiveBackend,
    int? PlacementOffloaded,
    int? PlacementTotal,
    string? ExecutableSha256,
    bool? HasAuxAssets,
    string KvCacheTypeSource);

public sealed record BenchmarkClaimedWork(
    long QueueSequence,
    Guid RunId,
    BenchmarkWorkKind Kind,
    int Attempt,
    long Version,
    BenchmarkRunRecord Run);

public abstract class BenchmarkStoreException(string message) : InvalidOperationException(message);

public sealed class BenchmarkNotFoundException(string message) : BenchmarkStoreException(message);

public sealed class BenchmarkConflictException(string code) : BenchmarkStoreException(code)
{
    public string Code { get; } = code;
}

public sealed class BenchmarkValidationException(string message) : BenchmarkStoreException(message);
