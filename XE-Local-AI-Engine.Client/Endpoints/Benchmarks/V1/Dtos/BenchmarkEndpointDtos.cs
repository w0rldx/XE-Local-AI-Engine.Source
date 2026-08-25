namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public enum BenchmarkErrorCode
{
    InvalidRequest,
    NotFound,
    VersionConflict,
    ProjectFrozen,
    ActiveRun,
    InvalidLifecycleTransition,
    FreezeDependencyChanged,
    FingerprintChanged,
    IneligibleAgent,
    IneligibleModel,
    UnsupportedSnapshot,
    UnsupportedKvCacheType,
    RejudgeRequired,
    JudgeAttemptsActive,
    JudgeAttemptActive,
    JudgePolicyAlreadyApplied,
    JudgePolicyChanged,
    JudgeDisabled,
    PrimaryNotSucceeded,

    /// <summary>Batch only: the cell never reached the freeze because an earlier cell stopped the batch.</summary>
    NotAttempted
}

public class BenchmarkProjectMutationRequest
{
    public string Name { get; init; } = string.Empty;
    public string CoreTask { get; init; } = string.Empty;
    public int ContextTokens { get; init; }

    /// <summary>Per-run output-token budget; omitted leaves generation context-limited. Must be &lt; ContextTokens.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Seconds one run's generation may take; omitted takes the node default (900). Range 60..7200.</summary>
    public int? InvocationTimeoutSeconds { get; init; }

    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public string? JudgeModelName { get; init; }
    public int? JudgeContextTokens { get; init; }

    /// <summary>Omitted takes the default rubric.</summary>
    public BenchmarkRubricDto? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }
}

public sealed class UpdateBenchmarkProjectRequest : BenchmarkProjectMutationRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class BenchmarkProjectRouteRequest
{
    public Guid ProjectId { get; init; }
}

public sealed class DeleteBenchmarkProjectRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public class BenchmarkProjectSummaryResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int ContextTokens { get; init; }

    /// <summary>Per-run output-token budget, or null when generation is context-limited.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Seconds one run's generation may take, or null for the node default.</summary>
    public int? InvocationTimeoutSeconds { get; init; }

    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public int RunCount { get; init; }
    public bool IsFrozen { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }
}

public sealed class BenchmarkProjectDetailResponse : BenchmarkProjectSummaryResponse
{
    public required string CoreTask { get; init; }

    /// <summary>The judge configuration this project judges under. <c>Enabled: false</c> when judging is off.</summary>
    public required BenchmarkJudgePolicyResponse Judge { get; init; }
}

public sealed class ListBenchmarkProjectsResponse
{
    public IReadOnlyList<BenchmarkProjectSummaryResponse> Items { get; init; } = [];
}

public sealed class ListBenchmarkRunsRequest
{
    public Guid ProjectId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    /// <summary>
    ///     Same-build history: only runs whose model CONTENT fingerprint equals this. Deliberately not the
    ///     <c>modelGroupKey</c> a run reports — that one is the base model, which spans several fingerprints.
    /// </summary>
    public string? ModelContentFingerprint { get; init; }

    /// <summary>False drops runs that carry no quality score at all.</summary>
    public bool IncludeUnscored { get; init; } = true;
}

/// <remarks>
///     <para>
///         With <see cref="RepeatCount" /> above 1, or with <see cref="Warmup" />, the node freezes ONCE and enqueues
///         several runs against that one snapshot, sharing a <c>repeatGroupId</c> and numbered by <c>repeatIndex</c>
///         (0 = the warm-up when one was asked for, then 1..N). They are enqueued back-to-back in FIFO order.
///     </para>
///     <para>
///         Each run still spawns its own exclusive <c>llama-server</c> — that is the unchanged design of the benchmark
///         queue, not an oversight — so repeats measure cold-launch-to-cold-launch jitter INCLUDING model load, which
///         is what an operator on this node actually experiences. And because the frozen sampling is deterministic
///         (temperature 0, fixed seed), identical launches produce the identical answer: what repeats quantify is
///         throughput jitter, not answer variance.
///     </para>
/// </remarks>
public sealed class StartBenchmarkRunRequest
{
    public Guid ProjectId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public long ExpectedProjectVersion { get; init; }

    /// <summary><c>f16</c>, <c>q8_0</c>, <c>q4_0</c>, or <see langword="null" /> for Auto (the node picks).</summary>
    public string? KvCacheType { get; init; }

    /// <summary>Measured runs to enqueue, 1..10. The response is the FIRST run of the group — the one that starts.</summary>
    public int RepeatCount { get; init; } = 1;

    /// <summary>Enqueue one extra run first, flagged as a warm-up: never ranked, never in a group's statistics.</summary>
    public bool Warmup { get; init; }
}

/// <summary>One cell of the launch matrix: a model, optionally pinned to a KV-cache type.</summary>
public sealed class StartBenchmarkRunBatchItem
{
    public string ModelName { get; init; } = string.Empty;

    /// <summary><c>f16</c>, <c>q8_0</c>, <c>q4_0</c>, or <see langword="null" /> for Auto.</summary>
    public string? KvCacheType { get; init; }
}

/// <summary>
///     Enqueues a whole model × KV-type matrix in one call. Deliberately NOT all-or-nothing: one ineligible model in a
///     ten-cell matrix must not cost the operator the other nine. Each cell goes through the same freeze path as the
///     single-run endpoint and reports its own outcome.
/// </summary>
public sealed class StartBenchmarkRunBatchRequest
{
    public Guid ProjectId { get; init; }

    /// <summary>The project version the whole batch is planned against; every later cell chains off it.</summary>
    public long ExpectedProjectVersion { get; init; }

    public IReadOnlyList<StartBenchmarkRunBatchItem> Items { get; init; } = [];

    /// <summary>Measured runs per item, 1..10.</summary>
    public int RepeatCount { get; init; } = 1;

    /// <summary>Prepend a warm-up run to every item's group.</summary>
    public bool Warmup { get; init; }
}

/// <summary>One matrix cell the node accepted, with the runs it enqueued in queue order.</summary>
public sealed class StartedBenchmarkRunBatchItemResponse
{
    public required string ModelName { get; init; }
    public string? KvCacheType { get; init; }

    /// <summary>Warm-up first when one was requested, then the measured repeats.</summary>
    public IReadOnlyList<Guid> RunIds { get; init; } = [];
}

/// <summary>One matrix cell the node refused, carrying the same <c>code</c> the single-run endpoint would have.</summary>
public sealed class RejectedBenchmarkRunBatchItemResponse
{
    public required string ModelName { get; init; }
    public string? KvCacheType { get; init; }

    /// <summary>A <see cref="BenchmarkErrorCode" /> name — the same vocabulary a single-run failure uses.</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }
}

public sealed class StartBenchmarkRunBatchResponse
{
    /// <summary>
    ///     The project version after the last cell that started — the version the NEXT batch has to present. It is the
    ///     only way a caller can resubmit the cells a partial batch left untried without re-reading the project.
    /// </summary>
    public long ProjectVersion { get; init; }

    public IReadOnlyList<StartedBenchmarkRunBatchItemResponse> Started { get; init; } = [];
    public IReadOnlyList<RejectedBenchmarkRunBatchItemResponse> Rejected { get; init; } = [];
}

public sealed class BenchmarkRunRouteRequest
{
    public Guid RunId { get; init; }
}

public sealed class DeleteBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class CancelBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public BenchmarkCancellationTarget Target { get; init; }
    public long ExpectedVersion { get; init; }
}

/// <remarks>
///     <see cref="Score" /> is nullable on purpose: a CLR-default <c>0</c> is a VALID operator score now, so an omitted
///     body field must be distinguishable from a deliberate zero and is rejected as a 400.
/// </remarks>
public sealed class ScoreBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public int? Score { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class ClearBenchmarkRunScoreRequest
{
    public Guid RunId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class RejudgeBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public long ExpectedVersion { get; init; }

    /// <summary>Judges again even when this run is already judged under the current policy and judge runtime.</summary>
    public bool Force { get; init; }
}

public sealed class RejudgeBenchmarkProjectRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class UpdateBenchmarkJudgePolicyRequest
{
    public Guid ProjectId { get; init; }

    /// <summary><see langword="null" /> disables judging; existing revisions and attempts stay as history.</summary>
    public BenchmarkJudgePolicyDraftDto? Policy { get; init; }

    public long ExpectedVersion { get; init; }

    /// <summary>Required when the policy actually changes on a project that already has runs.</summary>
    public bool ConfirmRejudge { get; init; }
}

public sealed class BenchmarkJudgePolicyDraftDto
{
    public string ModelName { get; init; } = string.Empty;
    public int ContextTokens { get; init; }

    /// <summary>Omitted takes the default rubric.</summary>
    public BenchmarkRubricDto? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }
}

public sealed class BenchmarkRubricCriterionDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Weight { get; init; }
}

public sealed class BenchmarkRubricDto
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<BenchmarkRubricCriterionDto> Criteria { get; init; } = [];
}

/// <summary>The judge configuration a project currently judges under, decrypted from its policy revision.</summary>
public sealed class BenchmarkJudgePolicyResponse
{
    public bool Enabled { get; init; }
    public Guid? PolicyRevisionId { get; init; }
    public int? PolicyRevision { get; init; }
    public string? PolicyHash { get; init; }
    public string? ModelName { get; init; }
    public int? RequestedContextTokens { get; init; }
    public BenchmarkRubricDto? Rubric { get; init; }
    public string? ReferenceAnswer { get; init; }
    public int? CohortGeneration { get; init; }
    public string? ReferenceExecutionKey { get; init; }

    /// <summary>The judge prompt version this revision was stored under.</summary>
    public int? PromptVersion { get; init; }

    /// <summary>
    ///     True when <see cref="PromptVersion" /> is not the one this build judges under. The revision still READS —
    ///     the project opens, the export works, existing scores stay ranked — but no NEW judging will run against it
    ///     until the operator re-saves the judge, which mints a revision under the current version and re-judges.
    /// </summary>
    public bool PromptVersionOutdated { get; init; }
}

/// <summary>The result of a judge change: the updated project plus the runs a judging was queued for.</summary>
public sealed class BenchmarkJudgeChangeResponse
{
    public required BenchmarkProjectDetailResponse Project { get; init; }
    public IReadOnlyList<Guid> EnqueuedRunIds { get; init; } = [];
    public int? CohortGeneration { get; init; }
}

public sealed class BenchmarkRubricPresetsResponse
{
    public required BenchmarkRubricDto Default { get; init; }
    public required BenchmarkRubricDto Programming { get; init; }
    public required BenchmarkRubricDto Reasoning { get; init; }
}

/// <summary>One rubric criterion as the judge scored it. Detail responses only.</summary>
public sealed class BenchmarkJudgeCriterionScoreResponse
{
    public required string Id { get; init; }
    public int Score { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
///     The run's judge state, derived from its current attempt. <see cref="Summary" /> and <see cref="Criteria" /> are
///     the decrypted verdict and appear on the detail response only — a list must not decrypt one blob per row.
/// </summary>
public sealed class BenchmarkRunJudgeResponse
{
    public required string State { get; init; }
    public int? Score { get; init; }
    public int? PolicyRevision { get; init; }
    public int? AttemptSequence { get; init; }
    public int? CohortGeneration { get; init; }
    public string? ExecutionKey { get; init; }
    public bool PolicyCurrent { get; init; }
    public bool ExecutionCurrent { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<BenchmarkJudgeCriterionScoreResponse>? Criteria { get; init; }
}

public class BenchmarkRunSummaryResponse
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string PrimaryModelName { get; init; }
    public LocalModelOrigin? PrimaryModelOrigin { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public required string AgentName { get; init; }
    public long AgentVersion { get; init; }
    public int RequestedContextTokens { get; init; }
    public BenchmarkPrimaryStatus PrimaryStatus { get; init; }
    public required BenchmarkRunJudgeResponse Judge { get; init; }

    /// <summary>The value this run ranks by: the operator override when set, otherwise the in-cohort judge score.</summary>
    public int? QualityScore { get; init; }

    /// <summary><c>user</c>, <c>judge</c>, or <c>none</c>.</summary>
    public required string QualityScoreSource { get; init; }

    /// <summary>Dense rank within the project, descending. Null when the run is not in the ranked cohort.</summary>
    public int? Rank { get; init; }

    /// <summary>
    ///     Why this run is not in the project's ranked cohort, or null when it is ranked. One of <c>no-score</c>,
    ///     <c>judge-pending</c>, <c>judge-failed</c>, <c>judge-cancelled</c>, <c>policy-outdated</c>,
    ///     <c>generation-stale</c>, <c>execution-key-mismatch</c>, <c>execution-identity-incomplete</c>,
    ///     <c>truncated</c>, <c>incomplete</c>, <c>warmup</c>.
    /// </summary>
    public string? RankExclusionReason { get; init; }

    /// <summary>
    ///     Why the primary generation stopped, verbatim from the provider (<c>stop</c>, <c>length</c>,
    ///     <c>tool_calls</c>, <c>content_filter</c>), or null when none was reported. <c>length</c> means the answer
    ///     was cut off by the token budget — the run still succeeded, but it does not rank.
    ///     <para>
    ///         <c>incomplete</c> is the one value the node derives rather than reads: the turn finished cleanly and
    ///         produced no answer at all — it ended on an unanswered tool call, or emitted only reasoning. It succeeds
    ///         and does not rank, exactly like <c>length</c>, and carries the <c>incomplete</c> rank-exclusion reason.
    ///     </para>
    /// </summary>
    public string? PrimaryStopReason { get; init; }

    /// <summary>
    ///     The BASE model this run is a build of — the Hugging Face repo id (lowercased) or the imported name, with the
    ///     quant tag stripped. Every quant of one model shares it, so a group is one model and its rows are its quants.
    ///     Use <see cref="ModelContentFingerprint" /> when you mean the exact build instead.
    /// </summary>
    public required string ModelGroupKey { get; init; }

    /// <summary>
    ///     The repeat group this run belongs to, or null for a plain single run. Runs sharing it were frozen together
    ///     against one snapshot and enqueued back-to-back.
    /// </summary>
    public Guid? RepeatGroupId { get; init; }

    /// <summary>Position in the group: 0 is the warm-up (only when one was requested), measured repeats are 1..N.</summary>
    public int? RepeatIndex { get; init; }

    /// <summary>A warm-up run: shown, but never ranked and never counted in a group's statistics.</summary>
    public bool IsWarmup { get; init; }

    public int? EffectiveContextTokens { get; init; }
    public long? DurationMs { get; init; }
    public int? TotalTokens { get; init; }

    /// <summary>
    ///     Decode throughput (tg) in tokens per second — derived from <see cref="GenerationTokens" /> and the runtime's
    ///     own decode duration whenever it reported them, so this is generation speed, not the blended
    ///     prompt-plus-generation figure it used to be. Falls back to <c>totalTokens / durationMs</c> for a runtime that
    ///     reports no per-request timings. Equal to <see cref="GenerationTokensPerSecond" /> when the split exists.
    /// </summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>
    ///     Time to first token in milliseconds, measured client-side from turn start — so it includes network and
    ///     adapter overhead on top of the runtime's own prefill time, which is what a caller actually waits. Null for a
    ///     runtime that reported nothing and for runs frozen before the column existed.
    /// </summary>
    public double? TtftMs { get; init; }

    /// <summary>Prompt tokens the runtime evaluated, cached ones included. Null when it reported none.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Prompt-processing throughput (pp) in tokens per second, derived server-side. Null when unmeasured.</summary>
    public double? PromptTokensPerSecond { get; init; }

    /// <summary>Tokens the runtime decoded. Null when it reported none.</summary>
    public int? GenerationTokens { get; init; }

    /// <summary>Decode throughput (tg) in tokens per second, derived server-side. Null when unmeasured.</summary>
    public double? GenerationTokensPerSecond { get; init; }

    /// <summary>
    ///     Prompt tokens served from the prompt cache rather than evaluated, across ALL of the turn's requests. Above
    ///     zero means the pp figures describe a partially cached prefill, not a cold one — expected on a tool-calling
    ///     turn, where every round re-sends the conversation and the runtime serves the shared prefix from cache.
    /// </summary>
    public int? CachedPromptTokens { get; init; }

    /// <summary>
    ///     How many provider requests the turn made, i.e. how many readings the token and millisecond sums are made of.
    ///     1 for a plain turn; above 1 means the agent called tools and each round prefilled again. Null when nothing
    ///     was measured.
    /// </summary>
    public int? SegmentCount { get; init; }

    public int? UserScore { get; init; }
    public long LastStreamSequence { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }

    /// <summary>What freeze intended this run to launch. All null for runs frozen before launch evidence existed.</summary>
    public string? PrimaryVariant { get; set; }

    public string? PrimaryKvCacheType { get; set; }
    public string? PrimaryKvCacheTypeSource { get; set; }
    public string? PrimaryKvAutoReason { get; set; }
    public string? PrimaryFlashAttentionMode { get; set; }
    public string? PrimaryIntendedLaunchIdentity { get; set; }
    public string? PrimaryIntendedExecutableSha256 { get; set; }

    /// <summary>What the launch itself recorded. All null until the spawn reached readiness.</summary>
    public string? PrimaryEffectiveLaunchIdentity { get; set; }

    public string? PrimaryEffectiveBackend { get; set; }
    public int? PrimaryPlacementOffloaded { get; set; }
    public int? PrimaryPlacementTotal { get; set; }
    public string? PrimaryExecutableSha256 { get; set; }
    public bool? PrimaryHasAuxAssets { get; set; }
    public string? PrimaryReceiptHash { get; set; }
    public string? PrimaryEnvironmentFactsHash { get; set; }
}

public sealed class BenchmarkRunDetailResponse : BenchmarkRunSummaryResponse
{
    public JsonElement? OutputParts { get; init; }

    /// <summary>The rubric verdict of the current attempt (<c>BenchmarkJudgeResultV2</c>), or null.</summary>
    public JsonElement? JudgeResult { get; init; }

    public string? PrimaryErrorMessage { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? PrimaryCompletedAtUtc { get; init; }

    /// <summary>The decoded launch receipt (<c>LlamaServerLaunchReceipt</c> v1), or null when none was recorded.</summary>
    public JsonElement? PrimaryLaunchReceipt { get; init; }

    /// <summary>The decoded pre-launch environment capture (<c>RuntimeEnvironmentFactsV1</c>), or null.</summary>
    public JsonElement? PrimaryEnvironmentFacts { get; init; }
}

public sealed class BenchmarkRankCohortResponse
{
    public int? PolicyRevision { get; init; }
    public string? ExecutionKey { get; init; }
    public int? CohortGeneration { get; init; }
    public int RankedCount { get; init; }
    public int TotalScored { get; init; }
}

public sealed class ListBenchmarkRunsResponse
{
    public IReadOnlyList<BenchmarkRunSummaryResponse> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }

    /// <summary>What the ranking is computed against, so the UI can say "n of m ranked" honestly.</summary>
    public required BenchmarkRankCohortResponse RankCohort { get; init; }
}

public sealed class EligibleBenchmarkAgentsRequest
{
    public string ModelName { get; init; } = string.Empty;
}

public sealed class EligibleBenchmarkAgentResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
}

public sealed class ListEligibleBenchmarkAgentsResponse
{
    public IReadOnlyList<EligibleBenchmarkAgentResponse> Items { get; init; } = [];
}

public sealed class EligibleBenchmarkModelsRequest
{
    public int? ContextTokens { get; init; }
}

public sealed class EligibleBenchmarkModelResponse
{
    public required string ModelName { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public LocalModelOrigin? Origin { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public bool SupportsTools { get; init; }
}

public sealed class ListEligibleBenchmarkModelsResponse
{
    public IReadOnlyList<EligibleBenchmarkModelResponse> Items { get; init; } = [];
}
