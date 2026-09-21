namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public sealed record BenchmarkStartRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid ProjectId { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    public required ReadOnlyMemory<byte> RuntimeSnapshotJson { get; init; }

    public required string PrimaryModelName { get; init; }

    public required LocalModelOrigin? PrimaryModelOrigin { get; init; }

    public required string ModelContentFingerprint { get; init; }

    public required string AgentName { get; init; }

    public required long AgentVersion { get; init; }

    public required int RequestedContextTokens { get; init; }

    public IBenchmarkFreezeCommitGuard? FreezeCommitGuard { get; init; }

    public BenchmarkRunLaunchIntent? PrimaryLaunchIntent { get; init; }

    public Guid? RepeatGroupId { get; init; }

    public int? RepeatIndex { get; init; }

    public bool IsWarmup { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    public BenchmarkRepeatMode RepeatMode { get; init; }

    public string? SamplingSeed { get; init; }

    public double? SamplingTemperature { get; init; }

    public Guid? TaskItemId { get; init; }

    public int? TaskItemIndex { get; init; }

    public string? CellKey { get; init; }

    public string? TaskInputHash { get; init; }

    public string? TaskItemSetHash { get; init; }
}

/// <summary>
///     Application-owned dependency guard executed by <see cref="IBenchmarkStore.StartRunAsync" /> inside the same
///     transaction that verifies the project version and inserts the run/work rows. Returning <see langword="false" />
///     aborts the transaction with <c>FreezeDependencyChanged</c>.
/// </summary>
public interface IBenchmarkFreezeCommitGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public sealed record BenchmarkPrimarySuccessCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedWorkVersion { get; init; }

    public required ReadOnlyMemory<byte> OutputPartsJson { get; init; }

    public required long LastStreamSequence { get; init; }

    public required int EffectiveContextTokens { get; init; }

    public required long DurationMs { get; init; }

    public required int? TotalTokens { get; init; }

    /// <summary>
    ///     Decode throughput (tg) when <see cref="Throughput" /> carries the split, otherwise the blended
    ///     <c>TotalTokens / DurationMs</c>. Same column, same name, same meaning for every existing reader.
    /// </summary>
    public required double? TokensPerSecond { get; init; }

    public string? PrimaryStopReason { get; init; }

    public BenchmarkJudgeAttemptSeed? JudgeAttempt { get; init; }

    /// <summary>The separated throughput measurement, or <see langword="null" /> when the runtime reported none.</summary>
    public BenchmarkRunThroughput? Throughput { get; init; }
}

/// <summary>
///     One run's separated throughput facts: how long the caller waited for the first token, and how the turn's tokens
///     and milliseconds split between prompt processing (pp) and generation (tg).
/// </summary>
/// <remarks>
///     Persisted as plaintext numerics alongside the blended figures the columns already carried, never instead of
///     them. Display only, by operator decision: no member of this record is a ranking input.
///     <see cref="CachedPromptTokens" /> above zero means <see cref="PromptMs" /> measured a partially cached prefill
///     rather than a cold one — it counts tokens served from the prompt cache across ALL of the turn's requests.
/// </remarks>
public sealed record BenchmarkRunThroughput
{
    public double? TtftMs { get; init; }

    public int? PromptTokens { get; init; }

    public double? PromptMs { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationMs { get; init; }

    public int? CachedPromptTokens { get; init; }

    /// <summary>
    ///     How many provider requests the turn made, i.e. how many readings the sums are made of.
    /// </summary>
    /// <remarks>
    ///     Null on runs recorded before the column existed; 1 for a plain turn; more once the agent called tools,
    ///     because each tool round is another request that re-sends the conversation and prefills again.
    /// </remarks>
    public int? SegmentCount { get; init; }

    /// <summary>Prompt-processing throughput (pp) in tokens per second, or null when either input is absent.</summary>
    public double? PromptTokensPerSecond => TokenThroughput.FromMilliseconds(PromptTokens, PromptMs);

    /// <summary>Decode throughput (tg) in tokens per second, or null when either input is absent.</summary>
    public double? GenerationTokensPerSecond => TokenThroughput.FromMilliseconds(GenerationTokens, GenerationMs);
}

public sealed record BenchmarkRunRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required ReadOnlyMemory<byte> RuntimeSnapshotJson { get; init; }

    public required string PrimaryModelName { get; init; }

    public required LocalModelOrigin? PrimaryModelOrigin { get; init; }

    public required string ModelContentFingerprint { get; init; }

    public required string AgentName { get; init; }

    public required long AgentVersion { get; init; }

    public required int RequestedContextTokens { get; init; }

    public required BenchmarkPrimaryStatus PrimaryStatus { get; init; }

    public required int? EffectiveContextTokens { get; init; }

    public required long? DurationMs { get; init; }

    public required int? TotalTokens { get; init; }

    public required double? TokensPerSecond { get; init; }

    public required ReadOnlyMemory<byte>? OutputPartsJson { get; init; }

    public required long LastStreamSequence { get; init; }

    public required int? UserScore { get; init; }

    public required string? PrimaryErrorMessage { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? PrimaryCompletedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public BenchmarkRunLaunchIntent? PrimaryLaunchIntent { get; init; }

    public BenchmarkRunLaunchEvidence? PrimaryLaunchEvidence { get; init; }

    public string? PrimaryStopReason { get; init; }

    /// <summary>
    ///     The derived judge view. Everything judge-related is now attempt-owned: a run is judged many times, so nothing
    ///     about a judging is stored on the run itself beyond the pointer to its current attempt.
    /// </summary>
    public BenchmarkRunJudgeView? Judge { get; init; }

    public int? QualityScore { get; init; }

    public string? QualityScoreSource { get; init; }

    public int? Rank { get; init; }

    public BenchmarkRunThroughput? Throughput { get; init; }

    public Guid? RepeatGroupId { get; init; }

    public int? RepeatIndex { get; init; }

    public bool IsWarmup { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    public BenchmarkRepeatMode RepeatMode { get; init; }

    public string? SamplingSeed { get; init; }

    public double? SamplingTemperature { get; init; }

    public BenchmarkRunFidelity? Fidelity { get; init; }

    public Guid? TaskItemId { get; init; }

    public int? TaskItemIndex { get; init; }

    public string? CellKey { get; init; }

    public string? TaskInputHash { get; init; }

    public string? TaskItemSetHash { get; init; }

    public int? CellQuality { get; init; }
}

/// <summary>
///     A run's quant-fidelity projection: a copy of the latest succeeded measurement.
/// </summary>
/// <remarks>
///     Display only — perplexity and KL divergence are never ranking inputs, and a KLD figure is shown only while
///     <see cref="KldBaseLogitsDigest" /> equals the digest the project's current settings recompute.
/// </remarks>
public sealed record BenchmarkRunFidelity
{
    public required string? Status { get; init; }

    public required Guid? AttemptId { get; init; }

    public required double? PerplexityMean { get; init; }

    public required double? PerplexityStdErr { get; init; }

    public required int? PerplexityChunks { get; init; }

    public required int? PerplexityContextTokens { get; init; }

    public required string? PerplexityCorpusId { get; init; }

    public required double? KldMean { get; init; }

    public required double? KldP99 { get; init; }

    public required double? TopTokenAgreement { get; init; }

    public required string? KldBaseFingerprint { get; init; }

    public required string? KldBaseLogitsDigest { get; init; }

    public required string? ErrorMessage { get; init; }
}

/// <summary>One immutable fidelity measurement of one run, as the attempt-history read serves it.</summary>
public sealed class BenchmarkFidelityAttemptRecord
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required int Sequence { get; init; }

    public required string Kind { get; init; }

    public required BenchmarkJudgeAttemptStatus Status { get; init; }

    public required double? PerplexityMean { get; init; }

    public required double? PerplexityStdErr { get; init; }

    public required int? PerplexityChunks { get; init; }

    public required int? PerplexityContextTokens { get; init; }

    public required string? CorpusId { get; init; }

    public required double? KldMean { get; init; }

    public required double? KldP99 { get; init; }

    public required double? TopTokenAgreement { get; init; }

    public required string? BaseModelName { get; init; }

    public required string? BaseModelContentFingerprint { get; init; }

    public required string? BaseLogitsDigest { get; init; }

    public required string? ErrorMessage { get; init; }

    public required long EnqueuedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }
}

/// <summary>
///     What freeze decided one phase of a run would launch with, before anything was spawned. Compared against the
///     evidence the launch itself recorded; the two differing is a fact the UI shows, not an error.
/// </summary>
public sealed record BenchmarkRunLaunchIntent
{
    public required string Variant { get; init; }

    public required string KvCacheType { get; init; }

    /// <summary><c>explicit</c> when the run asked for this type, <c>auto</c> when freeze picked it.</summary>
    public required string KvCacheTypeSource { get; init; }

    /// <summary>Why Auto did not pick the quantized type, or <see langword="null" /> when it did.</summary>
    public required string? KvAutoReason { get; init; }

    public required string FlashAttentionMode { get; init; }

    public required string IntendedLaunchIdentity { get; init; }

    public required string? IntendedExecutableSha256 { get; init; }

    /// <summary>
    ///     The <c>LlamaServerLaunchProjection.IdentitySchemeVersion</c> <see cref="IntendedLaunchIdentity" /> was
    ///     computed under, stamped at freeze and never recomputed.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> on a row frozen before the scheme was recorded, which reads as scheme <c>1</c>. A
    ///     hash from one scheme says nothing about a hash from another, so work that straddles a change is failed
    ///     rather than compared.
    /// </remarks>
    public int? LaunchIdentityScheme { get; init; }
}

/// <summary>
///     The durable launch evidence recorded for one phase. <see cref="ReceiptJson" /> is null when the spawn never
///     reached readiness — the environment capture is still recorded, because a failed launch is exactly when the
///     host facts matter.
/// </summary>
public sealed record BenchmarkRunLaunchEvidence
{
    public required ReadOnlyMemory<byte>? ReceiptJson { get; init; }

    public required ReadOnlyMemory<byte>? EnvironmentFactsJson { get; init; }

    public required string? ReceiptHash { get; init; }

    public required string? EnvironmentFactsHash { get; init; }

    public required string? EffectiveLaunchIdentity { get; init; }

    public required string? EffectiveBackend { get; init; }

    public required int? PlacementOffloaded { get; init; }

    public required int? PlacementTotal { get; init; }

    public required string? ExecutableSha256 { get; init; }

    public required bool? HasAuxAssets { get; init; }

    public required string? KvCacheTypeSource { get; init; }
}

/// <summary>
///     Everything a run's durable launch-ready checkpoint records about what actually launched: the provider-owned
///     receipt and the pre-launch environment facts (canonical JSON, encrypted at rest), their hashes and flat columns.
/// </summary>
/// <remarks>
///     Deliberately strings, integers and flags only — the list and compare views read every column here without
///     decrypting or parsing the receipt payload. The receipt is assembled in the llama-server provider and serialized
///     before it reaches the store, so persisting it never drags a provider type through the store contract. Every
///     receipt-derived member is null together when the spawn failed before readiness.
/// </remarks>
public sealed class BenchmarkLaunchReceiptCommand
{
    public required string? ReceiptJson { get; init; }

    public required string EnvironmentFactsJson { get; init; }

    public required string EnvironmentFactsHash { get; init; }

    public required string? ReceiptHash { get; init; }

    public required string? EffectiveLaunchIdentity { get; init; }

    public required string? EffectiveBackend { get; init; }

    public required int? PlacementOffloaded { get; init; }

    public required int? PlacementTotal { get; init; }

    public required string? ExecutableSha256 { get; init; }

    public required bool? HasAuxAssets { get; init; }

    public required string KvCacheTypeSource { get; init; }
}

/// <summary>
///     What the project's ranking is currently computed against, including how many scored runs currently rank.
/// </summary>
public sealed class BenchmarkRankCohort
{
    public required int? PolicyRevision { get; init; }

    public required string? ExecutionKey { get; init; }

    public required int? CohortGeneration { get; init; }

    public required int RankedCount { get; init; }

    public required int TotalScored { get; init; }
}

public sealed class BenchmarkRunPage
{
    public required IReadOnlyList<BenchmarkRunRecord> Items { get; init; }

    /// <summary>Runs matching the filter, not in this page.</summary>
    public required int TotalCount { get; init; }

    public BenchmarkRankCohort? RankCohort { get; init; }
}

/// <summary>Where a run's ranking value came from.</summary>
public static class BenchmarkQualityScoreSources
{
    public const string User = "user";
    public const string Judge = "judge";

    /// <summary>The Bradley–Terry strength read out of the cohort's active fit, in a project judging pairwise.</summary>
    public const string Pairwise = "pairwise";

    public const string None = "none";
}

public sealed record BenchmarkClaimedWork
{
    public required long QueueSequence { get; init; }

    public required Guid RunId { get; init; }

    public required BenchmarkWorkKind Kind { get; init; }

    public required int Attempt { get; init; }

    public required long Version { get; init; }

    public required BenchmarkRunRecord Run { get; init; }

    public Guid? JudgeAttemptId { get; init; }

    public Guid? FidelityAttemptId { get; init; }

    public Guid? ComparisonId { get; init; }
}
