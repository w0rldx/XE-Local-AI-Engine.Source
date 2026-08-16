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
    /// <summary>
    ///     One page of a project's runs, newest first, carrying only what a summary needs. The encrypted payload
    ///     columns are NOT read: a list of runs must not decrypt every snapshot, output and receipt on the way to
    ///     rendering a table.
    /// </summary>
    /// <param name="modelGroupKey">Only runs of this model content fingerprint (same-model history), or null for all.</param>
    /// <param name="includeUnscored">False drops runs that carry no quality score at all.</param>
    Task<BenchmarkRunPage> ListRunsAsync(Guid projectId,
        int skip,
        int take,
        string? modelGroupKey = null,
        bool includeUnscored = true,
        CancellationToken cancellationToken = default);

    /// <summary>How many runs a project has, counted in the database.</summary>
    Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default);
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
    /// <param name="attemptId">The judge attempt this launch belongs to — judge evidence lives on the attempt, not the run.</param>
    /// <param name="judgeExecutionKey">
    ///     The rank-cohort key computed from this launch's effective evidence, or <see langword="null" /> when the
    ///     execution identity was incomplete (fail-closed: such an attempt is never ranked).
    /// </param>
    Task<bool> MarkJudgeLaunchReadyAsync(Guid attemptId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
    /// <summary>
    ///     Sets or clears the operator's 0..100 override. <see langword="null" /> clears it; the judge score stays
    ///     visible beside it either way.
    /// </summary>
    Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int? score, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        _ = await RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Points the project at the judge policy with this hash, creating the revision when the project has never
    ///     seen it, and resets that revision's rank cohort (reference key cleared, generation bumped) so only attempts
    ///     enqueued after this call can define it. Activating the hash the project is already on is a no-op that
    ///     resets nothing. Refused while any attempt of the project is Queued or Running, so a reset always covers the
    ///     complete eligible set.
    /// </summary>
    /// <returns>The active revision, whether this call created it, and the runs the caller should re-judge.</returns>
    Task<BenchmarkJudgePolicyActivation> ActivateJudgePolicyAsync(Guid projectId,
        long expectedProjectVersion,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Turns judging off by clearing the project's revision pointer. Revisions and attempts stay as history.
    ///     Refused while any attempt of the project is Queued or Running.
    /// </summary>
    Task DisableJudgePolicyAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default);

    /// <summary>One judge attempt by id, payloads included, or null when it is gone.</summary>
    Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>One judge policy revision by id, payload included, or null when it is gone.</summary>
    Task<BenchmarkJudgePolicyRevisionRecord?> GetJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>The revision the project judges under, payload included, or null when judging is off.</summary>
    Task<BenchmarkJudgePolicyRevisionRecord?> GetCurrentJudgePolicyRevisionAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every revision the project has ever activated, oldest first. The encrypted policy payload is deliberately
    ///     NOT read: listing revisions must not decrypt one blob per row.
    /// </summary>
    Task<IReadOnlyList<BenchmarkJudgePolicyRevisionRecord>> ListJudgePolicyRevisionsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Queues one more judging of a succeeded run under the project's current policy revision, as a new attempt
    ///     plus its work item, in one transaction.
    /// </summary>
    /// <exception cref="BenchmarkJudgePolicyChangedException">
    ///     The revision named by the command is no longer the project's current one; the caller re-resolves and retries.
    /// </exception>
    Task<BenchmarkJudgeAttemptRecord> EnqueueJudgeAttemptAsync(BenchmarkEnqueueJudgeAttemptCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Claims the rank cohort for <paramref name="executionKey" />: an insert-if-null compare-and-swap that only
    ///     succeeds while the revision still carries <paramref name="cohortGeneration" /> and no key yet. Called from
    ///     inside the transaction that stores a successful attempt's result, never at launch readiness — a failed
    ///     first attempt must not poison the cohort.
    /// </summary>
    /// <returns><see langword="true" /> when this call promoted the key.</returns>
    Task<bool> TryPromoteReferenceExecutionKeyAsync(Guid revisionId,
        int cohortGeneration,
        string executionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens a project-wide re-judge: refuses while any attempt of the project is active, resets the current
    ///     revision's cohort (reference key cleared, generation bumped), bumps the project version, and returns the
    ///     succeeded runs the caller must enqueue attempts for. The reset and the eligible set are decided in one
    ///     transaction, so a re-judge is never partial.
    /// </summary>
    Task<BenchmarkJudgePolicyActivation> BeginProjectRejudgeAsync(Guid projectId,
        long expectedProjectVersion,
        CancellationToken cancellationToken = default);
}

public sealed record BenchmarkProjectInput(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId);

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
    IBenchmarkFreezeCommitGuard? FreezeCommitGuard = null,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null);

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
    double? TokensPerSecond,
    BenchmarkJudgeAttemptSeed? JudgeAttempt = null);

/// <summary>
///     What the run executor resolved for the automatic first judging, carried into the same transaction that commits
///     primary success so a crash can never leave a succeeded run without its attempt.
/// </summary>
/// <param name="ExpectedJudgePolicyRevisionId">
///     The revision <see cref="RuntimeJson" /> was resolved for. When the project has moved on the store rolls back and
///     throws <see cref="BenchmarkJudgePolicyChangedException" />, so the caller can re-resolve and retry.
/// </param>
/// <param name="RuntimeJson">
///     The judge's frozen launch configuration. <see langword="null" /> means resolution failed, and the attempt is
///     inserted directly as Failed together with a terminal work item.
/// </param>
public sealed record BenchmarkJudgeAttemptSeed(
    Guid? ExpectedJudgePolicyRevisionId = null,
    ReadOnlyMemory<byte>? RuntimeJson = null,
    string? RuntimeUnresolvedReason = null,
    BenchmarkRunLaunchIntent? LaunchIntent = null);

/// <inheritdoc cref="IBenchmarkStore.EnqueueJudgeAttemptAsync" />
/// <param name="Force">Bypasses the already-applied guard for a deliberate operator re-judge.</param>
public sealed record BenchmarkEnqueueJudgeAttemptCommand(
    Guid RunId,
    long ExpectedRunVersion,
    Guid PolicyRevisionId,
    ReadOnlyMemory<byte>? RuntimeJson = null,
    string? RuntimeUnresolvedReason = null,
    bool Force = false,
    BenchmarkRunLaunchIntent? LaunchIntent = null);

/// <param name="PolicyJson">Null on a listing, which never decrypts the payload.</param>
public sealed record BenchmarkJudgePolicyRevisionRecord(
    Guid Id,
    Guid ProjectId,
    int Revision,
    ReadOnlyMemory<byte>? PolicyJson,
    string PolicyHash,
    string? ReferenceExecutionKey,
    int CohortGeneration,
    long CreatedAtUtc);

/// <param name="SucceededRunIds">
///     The project's succeeded runs with stored output, i.e. exactly what the caller should enqueue attempts for.
///     Empty on a no-op activation.
/// </param>
public sealed record BenchmarkJudgePolicyActivation(
    BenchmarkJudgePolicyRevisionRecord Revision,
    bool WasCreated,
    IReadOnlyList<Guid> SucceededRunIds);

public sealed record BenchmarkJudgeAttemptRecord(
    Guid Id,
    Guid RunId,
    int Sequence,
    Guid PolicyRevisionId,
    int CohortGeneration,
    ReadOnlyMemory<byte>? JudgeRuntimeJson,
    string? JudgeExecutionKey,
    BenchmarkJudgeAttemptStatus Status,
    ReadOnlyMemory<byte>? ResultJson,
    int? Score,
    string? ErrorMessage,
    long EnqueuedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Version,
    BenchmarkRunLaunchIntent? LaunchIntent = null,
    BenchmarkRunLaunchEvidence? LaunchEvidence = null);

/// <param name="Score">The server-computed 0..100 rubric score stored on the attempt, plaintext and sortable.</param>
public sealed record BenchmarkJudgeSuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> JudgeResultJson,
    long LastStreamSequence = 0,
    int? Score = null);

/// <param name="JudgeEnabled">Derived: the project judges exactly while it points at a policy revision.</param>
public sealed record BenchmarkProjectRecord(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    Guid? CurrentJudgePolicyRevisionId,
    bool IsFrozen,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <param name="Judge">
///     The derived judge view. Everything judge-related is now attempt-owned: a run is judged many times, so nothing
///     about a judging is stored on the run itself beyond the pointer to its current attempt.
/// </param>
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
    string? PrimaryErrorMessage,
    long Version,
    long CreatedAtUtc,
    long? StartedAtUtc,
    long? PrimaryCompletedAtUtc,
    long UpdatedAtUtc,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    BenchmarkRunLaunchEvidence? PrimaryLaunchEvidence = null,
    BenchmarkRunJudgeView? Judge = null,
    int? QualityScore = null,
    string? QualityScoreSource = null,
    int? Rank = null);

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

/// <summary>
///     The run-level judge state the API shows, derived from the run's current attempt and the project's current policy
///     revision. Nothing here is stored: a policy or runtime change must re-derive it, never re-label a stored value.
/// </summary>
/// <param name="State"><c>none</c> when there is no attempt, otherwise the current attempt's status, lowercased.</param>
/// <param name="RankExclusionReason">
///     Why this run is not in the ranked cohort, or <see langword="null" /> when it is ranked. One of <c>no-score</c>,
///     <c>judge-pending</c>, <c>judge-failed</c>, <c>policy-outdated</c>, <c>generation-stale</c>,
///     <c>execution-key-mismatch</c>, <c>execution-identity-incomplete</c>.
/// </param>
public sealed record BenchmarkRunJudgeView(
    string State,
    Guid? AttemptId,
    int? Score,
    int? PolicyRevision,
    Guid? PolicyRevisionId,
    int? AttemptSequence,
    int? CohortGeneration,
    string? ExecutionKey,
    string? ErrorMessage,
    bool PolicyCurrent,
    bool ExecutionCurrent,
    string? RankExclusionReason);

/// <summary>The <see cref="BenchmarkRunJudgeView.State" /> and <see cref="BenchmarkRunJudgeView.RankExclusionReason" /> vocabularies.</summary>
public static class BenchmarkRunJudgeStates
{
    public const string None = "none";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public const string ReasonNoScore = "no-score";
    public const string ReasonJudgePending = "judge-pending";
    public const string ReasonJudgeFailed = "judge-failed";
    public const string ReasonPolicyOutdated = "policy-outdated";
    public const string ReasonGenerationStale = "generation-stale";
    public const string ReasonExecutionKeyMismatch = "execution-key-mismatch";
    public const string ReasonExecutionIdentityIncomplete = "execution-identity-incomplete";
}

/// <summary>
///     What the project's ranking is currently computed against. The UI needs it to say "n of m ranked" honestly —
///     a project mid-re-judge, or one whose judge runtime moved, shows fewer ranked rows on purpose.
/// </summary>
public sealed record BenchmarkRankCohort(
    int? PolicyRevision,
    string? ExecutionKey,
    int? CohortGeneration,
    int RankedCount,
    int TotalScored);

/// <param name="TotalCount">Runs matching the filter, not in this page.</param>
public sealed record BenchmarkRunPage(IReadOnlyList<BenchmarkRunRecord> Items, int TotalCount, BenchmarkRankCohort? RankCohort = null);

/// <summary>Where a run's ranking value came from.</summary>
public static class BenchmarkQualityScoreSources
{
    public const string User = "user";
    public const string Judge = "judge";
    public const string None = "none";
}

public sealed record BenchmarkClaimedWork(
    long QueueSequence,
    Guid RunId,
    BenchmarkWorkKind Kind,
    int Attempt,
    long Version,
    BenchmarkRunRecord Run,
    Guid? JudgeAttemptId = null);

public abstract class BenchmarkStoreException(string message) : InvalidOperationException(message);

public sealed class BenchmarkNotFoundException(string message) : BenchmarkStoreException(message);

public sealed class BenchmarkConflictException(string code) : BenchmarkStoreException(code)
{
    public string Code { get; } = code;
}

public sealed class BenchmarkValidationException(string message) : BenchmarkStoreException(message);

/// <summary>
///     The project's judge policy moved while a judging was being prepared for the previous revision. Retryable: the
///     caller re-reads the current revision, re-resolves the judge runtime and calls again.
/// </summary>
public sealed class BenchmarkJudgePolicyChangedException(string message) : BenchmarkStoreException(message);
