namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

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
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    Guid? RepeatGroupId = null,
    int? RepeatIndex = null,
    bool IsWarmup = false,
    int? InvocationTimeoutSeconds = null,
    BenchmarkRepeatMode RepeatMode = BenchmarkRepeatMode.Throughput,
    string? SamplingSeed = null,
    double? SamplingTemperature = null,
    Guid? TaskItemId = null,
    int? TaskItemIndex = null,
    string? CellKey = null,
    string? TaskInputHash = null,
    string? TaskItemSetHash = null);

/// <summary>
///     Application-owned dependency guard executed by <see cref="IBenchmarkStore.StartRunAsync" /> inside the same
///     transaction that verifies the project version and inserts the run/work rows. Returning <see langword="false" />
///     aborts the transaction with <c>FreezeDependencyChanged</c>.
/// </summary>
public interface IBenchmarkFreezeCommitGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

/// <param name="TokensPerSecond">
///     Decode throughput (tg) when <paramref name="Throughput" /> carries the split, otherwise the blended
///     <c>TotalTokens / DurationMs</c>. Same column, same name, same meaning for every existing reader.
/// </param>
/// <param name="Throughput">
///     The separated throughput measurement, or <see langword="null" /> when the runtime reported none.
/// </param>
public sealed record BenchmarkPrimarySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> OutputPartsJson,
    long LastStreamSequence,
    int EffectiveContextTokens,
    long DurationMs,
    int? TotalTokens,
    double? TokensPerSecond,
    string? PrimaryStopReason = null,
    BenchmarkJudgeAttemptSeed? JudgeAttempt = null,
    BenchmarkRunThroughput? Throughput = null);

/// <summary>
///     One run's separated throughput facts: how long the caller waited for the first token, and how the turn's tokens
///     and milliseconds split between prompt processing (pp) and generation (tg). Persisted as plaintext numerics
///     alongside the blended figures the columns already carried, never instead of them.
///     <para>
///         Display only, by operator decision: no member of this record is a ranking input. <see cref="CachedPromptTokens" />
///         above zero means <see cref="PromptMs" /> measured a partially cached prefill rather than a cold one — it
///         counts tokens served from the prompt cache across ALL of the turn's requests.
///     </para>
/// </summary>
/// <param name="SegmentCount">
///     How many provider requests the turn made, i.e. how many readings the sums are made of. Null on runs recorded
///     before the column existed; 1 for a plain turn; more once the agent called tools, because each tool round is
///     another request that re-sends the conversation and prefills again.
/// </param>
public sealed record BenchmarkRunThroughput(
    double? TtftMs = null,
    int? PromptTokens = null,
    double? PromptMs = null,
    int? GenerationTokens = null,
    double? GenerationMs = null,
    int? CachedPromptTokens = null,
    int? SegmentCount = null)
{
    /// <summary>Prompt-processing throughput (pp) in tokens per second, or null when either input is absent.</summary>
    public double? PromptTokensPerSecond => TokenThroughput.FromMilliseconds(PromptTokens, PromptMs);

    /// <summary>Decode throughput (tg) in tokens per second, or null when either input is absent.</summary>
    public double? GenerationTokensPerSecond => TokenThroughput.FromMilliseconds(GenerationTokens, GenerationMs);
}

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
    string? PrimaryStopReason = null,
    BenchmarkRunJudgeView? Judge = null,
    int? QualityScore = null,
    string? QualityScoreSource = null,
    int? Rank = null,
    BenchmarkRunThroughput? Throughput = null,
    Guid? RepeatGroupId = null,
    int? RepeatIndex = null,
    bool IsWarmup = false,
    int? InvocationTimeoutSeconds = null,
    BenchmarkRepeatMode RepeatMode = BenchmarkRepeatMode.Throughput,
    string? SamplingSeed = null,
    double? SamplingTemperature = null,
    BenchmarkRunFidelity? Fidelity = null,
    Guid? TaskItemId = null,
    int? TaskItemIndex = null,
    string? CellKey = null,
    string? TaskInputHash = null,
    string? TaskItemSetHash = null,
    int? CellQuality = null);

/// <summary>
///     A run's quant-fidelity projection: a copy of the latest succeeded measurement. Display only — perplexity and
///     KL divergence are never ranking inputs, and a KLD figure is shown only while
///     <paramref name="KldBaseLogitsDigest" /> equals the digest the project's current settings recompute.
/// </summary>
public sealed record BenchmarkRunFidelity(
    string? Status,
    Guid? AttemptId,
    double? PerplexityMean,
    double? PerplexityStdErr,
    int? PerplexityChunks,
    int? PerplexityContextTokens,
    string? PerplexityCorpusId,
    double? KldMean,
    double? KldP99,
    double? TopTokenAgreement,
    string? KldBaseFingerprint,
    string? KldBaseLogitsDigest,
    string? ErrorMessage);

/// <summary>One immutable fidelity measurement of one run, as the attempt-history read serves it.</summary>
public sealed record BenchmarkFidelityAttemptRecord(
    Guid Id,
    Guid RunId,
    int Sequence,
    string Kind,
    BenchmarkJudgeAttemptStatus Status,
    double? PerplexityMean,
    double? PerplexityStdErr,
    int? PerplexityChunks,
    int? PerplexityContextTokens,
    string? CorpusId,
    double? KldMean,
    double? KldP99,
    double? TopTokenAgreement,
    string? BaseModelName,
    string? BaseModelContentFingerprint,
    string? BaseLogitsDigest,
    string? ErrorMessage,
    long EnqueuedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc);

/// <summary>
///     What freeze decided one phase of a run would launch with, before anything was spawned. Compared against the
///     evidence the launch itself recorded; the two differing is a fact the UI shows, not an error.
/// </summary>
/// <param name="KvCacheTypeSource"><c>explicit</c> when the run asked for this type, <c>auto</c> when freeze picked it.</param>
/// <param name="KvAutoReason">Why Auto did not pick the quantized type, or <see langword="null" /> when it did.</param>
/// <param name="LaunchIdentityScheme">
///     The <c>LlamaServerLaunchProjection.IdentitySchemeVersion</c> <paramref name="IntendedLaunchIdentity" /> was
///     computed under, stamped at freeze and never recomputed. <see langword="null" /> on a row frozen before the
///     scheme was recorded, which reads as scheme <c>1</c>. A hash from one scheme says nothing about a hash from
///     another, so work that straddles a change is failed rather than compared.
/// </param>
public sealed record BenchmarkRunLaunchIntent(
    string Variant,
    string KvCacheType,
    string KvCacheTypeSource,
    string? KvAutoReason,
    string FlashAttentionMode,
    string IntendedLaunchIdentity,
    string? IntendedExecutableSha256,
    int? LaunchIdentityScheme = null);

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
///     What the project's ranking is currently computed against, including how many scored runs currently rank.
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

    /// <summary>The Bradley–Terry strength read out of the cohort's active fit, in a project judging pairwise.</summary>
    public const string Pairwise = "pairwise";

    public const string None = "none";
}

public sealed record BenchmarkClaimedWork(
    long QueueSequence,
    Guid RunId,
    BenchmarkWorkKind Kind,
    int Attempt,
    long Version,
    BenchmarkRunRecord Run,
    Guid? JudgeAttemptId = null,
    Guid? FidelityAttemptId = null,
    Guid? ComparisonId = null);
