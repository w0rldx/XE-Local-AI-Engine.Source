namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IBenchmarkRunExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

public interface IBenchmarkJudgeExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

public enum BenchmarkRunStreamEventKind
{
    OutputDelta,
    ReasoningDelta,
    ToolCall,
    ToolResult,
    PrimaryState,
    JudgeState,
    Metrics,
    TerminalSnapshotAvailable
}

public sealed record BenchmarkRunStreamPayload
{
    public string? Content { get; init; }

    public string? State { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? Arguments { get; init; }

    public string? Result { get; init; }

    public bool? IsError { get; init; }

    public int? EffectiveContextTokens { get; init; }

    public long? DurationMs { get; init; }

    public int? TotalTokens { get; init; }

    public double? TokensPerSecond { get; init; }

    public long? RunVersion { get; init; }

    public double? TtftMs { get; init; }

    public int? PromptTokens { get; init; }

    public double? PromptTokensPerSecond { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationTokensPerSecond { get; init; }

    public int? CachedPromptTokens { get; init; }

    public int? SegmentCount { get; init; }
}

public sealed record BenchmarkRunStreamEvent
{
    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }

    public required BenchmarkRunStreamEventKind Kind { get; init; }

    public required BenchmarkRunStreamPayload Payload { get; init; }
}

public sealed class BenchmarkRunStreamEventArgs : EventArgs
{
    public BenchmarkRunStreamEventArgs(BenchmarkRunStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        StreamEvent = streamEvent;
    }

    public BenchmarkRunStreamEvent StreamEvent { get; }
}

public sealed class BenchmarkReplayResult
{
    public required IReadOnlyList<BenchmarkRunStreamEvent> Events { get; init; }

    public required bool ResetRequired { get; init; }

    public required long LatestSequence { get; init; }

    public required long RunVersion { get; init; }
}

public interface IBenchmarkEventBuffer
{
    event EventHandler<BenchmarkRunStreamEventArgs>? EventPublished;

    BenchmarkRunStreamEvent Append(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload);
    BenchmarkRunStreamEvent Reserve(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload);
    void PublishReserved(BenchmarkRunStreamEvent streamEvent);
    BenchmarkReplayResult Replay(Guid runId, long afterSequence, long runVersion);
    void BeginActivePhase(Guid runId, long persistedSequence);
    void EvictPlaintext(Guid runId);
}

public sealed class BenchmarkEventBufferOptions
{
    public const int DefaultMaxEventCount = 512;
    public const int DefaultMaxUtf8Bytes = 1024 * 1024;

    /// <summary>How many TERMINAL runs keep their (already emptied) buffer entry.</summary>
    /// <remarks>
    ///     The entry carries no output — eviction cleared that — only the sequence bookkeeping that lets a late
    ///     subscriber be told to reset instead of being answered with silence. Past this many the oldest are dropped:
    ///     the hub then compares the run's persisted <c>LastStreamSequence</c> against an empty replay and resets
    ///     anyway, which is the same answer by another route. Active runs are never dropped, however many there are.
    /// </remarks>
    public const int DefaultMaxRetainedTerminalRuns = 256;

    public int MaxEventCount { get; init; } = DefaultMaxEventCount;
    public int MaxUtf8Bytes { get; init; } = DefaultMaxUtf8Bytes;
    public int MaxRetainedTerminalRuns { get; init; } = DefaultMaxRetainedTerminalRuns;
}

public sealed record BenchmarkOutputPart(
    string Kind,
    string? Content = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    bool? IsError = null);

public sealed class BenchmarkEligibleAgent
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Version { get; init; }
}

public sealed class BenchmarkEligibleModel
{
    public required string ModelName { get; init; }

    public required int? MaxContextTokens { get; init; }

    public required int? EffectiveContextTokens { get; init; }

    public required LocalModelOrigin? Origin { get; init; }

    public required string ModelContentFingerprint { get; init; }

    public required bool SupportsTools { get; init; }
}

/// <summary>
///     The operator-editable judge configuration. Everything here is inside the policy hash, so any change to it is a
///     new policy revision and — on a project that already has runs — a re-judge.
/// </summary>
public sealed record BenchmarkJudgePolicyDraft
{
    public required string ModelName { get; init; }

    public required int ContextTokens { get; init; }

    /// <summary>The weighted criteria; <see langword="null" /> takes <see cref="BenchmarkJudgeRubricDefaults.Default" />.</summary>
    public BenchmarkJudgeRubricV1? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }

    /// <summary>
    ///     <c>pointwise</c> (the default and the only mode this build executes) or <c>pairwise</c>. Absent means
    ///     pointwise, so a caller written before the mode existed keeps working.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
///     The four settable quant-fidelity knobs. The base model's FINGERPRINT is absent on purpose: the service resolves
///     it from the eligible-model catalog, because it is an input to the KLD comparability digest.
/// </summary>
public sealed class BenchmarkProjectFidelitySettings
{
    public required bool Enabled { get; init; }

    public required bool KldEnabled { get; init; }

    public required int? Chunks { get; init; }

    public required string? KldBaseModelName { get; init; }
}

public sealed record BenchmarkProjectDraft
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string CoreTask { get; init; }

    public required int ContextTokens { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public BenchmarkJudgePolicyDraft? Judge { get; init; }

    /// <summary>
    ///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
    ///     context-limited. Validated as <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    /// <summary>
    ///     The per-run thinking budget frozen into every run's sampling, or <see langword="null" /> to leave the
    ///     reasoning bounded only by the effort ladder and the window.
    /// </summary>
    /// <remarks>
    ///     Validated as <c>1 &lt;= ReasoningBudgetTokens &lt; ContextTokens</c>, and — with an output budget also set —
    ///     as leaving a prompt reserve inside the context.
    /// </remarks>
    public int? ReasoningBudgetTokens { get; init; }

    public bool FidelityEnabled { get; init; }

    public bool FidelityKldEnabled { get; init; }

    public int? FidelityChunks { get; init; }

    /// <summary>
    ///     The base model KL divergence is measured against. Its FINGERPRINT is never part of a draft: the service
    ///     resolves it from the eligible-model catalog, so a caller cannot claim a base identity the node does not have.
    /// </summary>
    public string? FidelityKldBaseModelName { get; init; }
}

public sealed class BenchmarkQueueOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Benchmarks:Queue";

    /// <summary>The longest poll interval that still lets a queued run start promptly after a signal is missed.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
///     One launch request: a project, a model, and how many measured runs to enqueue against them.
/// </summary>
public sealed record BenchmarkRunStartRequest
{
    public required Guid ProjectId { get; init; }

    public required string PrimaryModelName { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    /// <summary>
    ///     The KV-cache type the run asked for, or <see langword="null" /> for Auto (freeze picks). Must already be
    ///     canonical — see <see cref="BenchmarkKvCacheType.TryNormalize" />.
    /// </summary>
    public string? KvCacheType { get; init; }

    /// <summary>
    ///     How many measured runs to enqueue, 1..<see cref="BenchmarkRunFreezeService.MaxRepeatCount" />. Everything but
    ///     the seed is frozen ONCE and the repeats share it.
    /// </summary>
    public int RepeatCount { get; init; } = 1;

    /// <summary>
    ///     Prepends one more run at repeat index 0, flagged <c>IsWarmup</c>: never ranked, never counted in a group's
    ///     statistics. It exists to absorb the first-launch costs (page cache cold, GPU clocks low) the measured repeats
    ///     should not pay.
    /// </summary>
    public bool Warmup { get; init; }

    /// <summary>
    ///     What the group measures. <see cref="BenchmarkRepeatMode.Throughput" /> is the default and the historical
    ///     behaviour: temperature 0, one fixed seed, so the answer is identical across repeats and only the machine varies.
    /// </summary>
    public BenchmarkRepeatMode RepeatMode { get; init; }

    /// <summary>
    ///     The temperature an <see cref="BenchmarkRepeatMode.AnswerVariance" /> group samples at, or
    ///     <see langword="null" /> for <see cref="BenchmarkRunFreezeService.DefaultAnswerVarianceTemperature" />. Ignored
    ///     in throughput mode, which is deterministic by definition.
    /// </summary>
    public double? AnswerVarianceTemperature { get; init; }
}

/// <summary>One model's freeze, decided but NOT written.</summary>
/// <remarks>
///     Every read a freeze takes — the verified model lease, the eligibility, the agent resolution, the project
///     version — has already happened and produced these commands;
///     <see cref="IBenchmarkRunFreezeService.CommitAsync" /> is the only step that touches the database. That split is
///     what lets a PAIR be validated on both sides before either side exists: committing one side first left the
///     caller with queued runs it was never told the ids of, and the only retry available duplicated that side.
/// </remarks>
public sealed class BenchmarkFrozenRunPlan
{
    public required Guid ProjectId { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkStartRunCommand> Commands { get; init; }
}

public sealed class BenchmarkRunBatchRequest
{
    public required Guid ProjectId { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchItem> Items { get; init; }

    public required int RepeatCount { get; init; }

    public required bool Warmup { get; init; }

    public required BenchmarkRepeatMode RepeatMode { get; init; }

    public required double? AnswerVarianceTemperature { get; init; }
}

public sealed class BenchmarkRunBatchItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }
}

public sealed class BenchmarkRunBatchStartedItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }

    public required IReadOnlyList<Guid> RunIds { get; init; }
}

public enum BenchmarkRunBatchRejectionKind
{
    Failure,
    NotAttempted,
    TimeBudget
}

public sealed class BenchmarkRunBatchRejectedItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }

    public required BenchmarkRunBatchRejectionKind Kind { get; init; }

    public required string Message { get; init; }

    public Exception? Failure { get; init; }
}

public sealed class BenchmarkRunBatchResult
{
    public required long ProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchStartedItem> Started { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchRejectedItem> Rejected { get; init; }
}

/// <summary>
///     One task item as an operator writes it. The index, revision and input hash are absent on purpose: a caller that
///     could name them could present an answer to an old question as an answer to the current one.
/// </summary>
public sealed class BenchmarkTaskItemDraft
{
    public required string Prompt { get; init; }

    public string? Kind { get; init; }

    public string? ReferenceAnswer { get; init; }

    /// <summary>
    ///     Per-criterion overrides of the judge policy's verifier config, keyed by criterion id. Carried opaquely here —
    ///     it can hold expected answers, which is why it is stored encrypted.
    /// </summary>
    public JsonElement? VerifierConfig { get; init; }

    /// <summary>The parameters a generator item expands into child cases. Null for a plain prompt.</summary>
    public JsonElement? GeneratorConfig { get; init; }

    public bool CountsTowardScore { get; init; } = true;
}

public sealed class BenchmarkJudgePolicyChange
{
    public required BenchmarkProjectRecord Project { get; init; }

    /// <summary>The runs a judging was queued for, in the order they were enqueued. Empty on a no-op.</summary>
    public required IReadOnlyList<Guid> EnqueuedRunIds { get; init; }

    public required int? CohortGeneration { get; init; }
}
