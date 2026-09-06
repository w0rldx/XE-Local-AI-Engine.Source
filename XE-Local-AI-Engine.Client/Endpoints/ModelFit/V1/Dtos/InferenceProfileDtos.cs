namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>
///     A node-local inference profile projected for transport (shared by the list, explore, freeze and invalidate
///     responses). It carries only the launch-arg facts and lifecycle metadata; the local-only machine key is
///     deliberately OMITTED — it must never leave the box. <see cref="Role" /> is the lowercase wire role
///     (<c>chat|embedding|reranker</c>) and <see cref="Status" /> is the lifecycle name (<c>Explored|Frozen|Stale</c>).
/// </summary>
public sealed class InferenceProfileViewDto
{
    public required Guid Id { get; init; }

    public required string ModelName { get; init; }

    /// <summary>Lowercase role the profile targets — <c>chat|embedding|reranker</c>.</summary>
    public required string Role { get; init; }

    public required string Backend { get; init; }

    public required string LlamacppBuild { get; init; }

    public required string Quant { get; init; }

    public required int CtxSize { get; init; }

    public int? NGpuLayers { get; init; }

    public string? TensorSplit { get; init; }

    public string? OverrideTensor { get; init; }

    public string? KvTypeK { get; init; }

    public string? KvTypeV { get; init; }

    public required bool FlashAttn { get; init; }

    public long? NParams { get; init; }

    public required bool IsMoe { get; init; }

    public int? ExpertCount { get; init; }

    /// <summary>Version of the launch-policy fingerprint schema, or null for a legacy profile.</summary>
    public int? LaunchPolicyFingerprintVersion { get; init; }

    /// <summary>Versioned strong/validation launch-policy fingerprint, or null for a legacy profile that is treated as stale.</summary>
    public string? LaunchPolicyFingerprint { get; init; }

    /// <summary>Global device-free VRAM captured at freeze; the only VRAM invalidation baseline.</summary>
    public long? GlobalFreeVramAtFreezeBytes { get; init; }

    /// <summary>Calling-process VRAM budget captured at freeze; diagnostic only.</summary>
    public long? ProcessBudgetVramAtFreezeBytes { get; init; }

    /// <summary>The lifecycle status name — <c>Explored|Frozen|Stale</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The id of the benchmark snapshot that justifies a freeze; null until benchmarked.</summary>
    public Guid? BenchmarkSnapshotId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/profiles</c>: every persisted inference profile (machine key omitted).</summary>
public sealed class ListInferenceProfilesResponse
{
    public required IReadOnlyList<InferenceProfileViewDto> Items { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/profiles/explore</c>. Explores a node-local GGUF model to draft its launch args.
///     <see cref="ModelName" /> must be non-blank and resolve to a local GGUF (a cloud or missing model is rejected with a
///     400). <see cref="Role" /> is <c>chat|embedding|reranker</c> (case-insensitive; defaults to <c>chat</c> when
///     omitted); an unknown role is rejected with a 400.
/// </summary>
public sealed class ExploreInferenceProfileRequest
{
    /// <summary>
    ///     Lowest accepted <see cref="ContextTokens" /> — the launch policy's smallest chat tier. A shape bound: the
    ///     resolver itself has no minimum beyond its 256-token alignment unit. Lives on the DTO so the documented range,
    ///     the <c>Range</c> annotation that publishes it in the OpenAPI schema, and the value
    ///     <c>ExploreInferenceProfileEndpoint</c> enforces cannot drift apart.
    /// </summary>
    internal const int MinExploreContextTokens = 2048;

    /// <summary>
    ///     Highest accepted <see cref="ContextTokens" />. A shape bound only; the model-specific ceiling belongs to the
    ///     resolver's cap-and-align step, which is why the endpoint must not try to guess it.
    /// </summary>
    internal const int MaxExploreContextTokens = 1_048_576;

    public required string ModelName { get; init; }

    /// <summary>Role to explore — <c>chat|embedding|reranker</c>. Defaults to <c>chat</c> when omitted.</summary>
    public string? Role { get; init; }

    /// <summary>
    ///     Optional operator benchmark knob: pins this explore spawn's context window (<c>-c</c>) for this call only.
    ///     Omit it for the allocation resolver's hardware-tier choice, which is what every explore did before this
    ///     field existed. Nothing about the value is persisted — it reaches one spawn and is never written to node
    ///     settings or the launch policy.
    ///     <para>
    ///         Accepted range is <see cref="MinExploreContextTokens" />–<see cref="MaxExploreContextTokens" />
    ///         (2048–1048576), enforced by <c>ExploreInferenceProfileEndpoint</c> from these same consts. The floor is
    ///         the launch policy's LOWEST chat tier rather than a resolver-enforced minimum (the resolver's own floor is
    ///         the 256-token alignment unit); the ceiling is a shape bound only. The model's train ceiling caps the
    ///         value SILENTLY, so a request above it succeeds with a smaller window: read <c>ctxSize</c> on the
    ///         returned profile for what was actually used.
    ///     </para>
    ///     <para>
    ///         An override the box cannot fit FAILS the explore rather than being reduced: it routes the allocation
    ///         down the deterministic-override branch, and <c>ProcessContextAllocationResolver.TryDownTierForAdmission</c>
    ///         down-tiers hardware-tier allocations only. So the value is honoured verbatim or the spawn is rejected;
    ///         an unoverridden explore, by contrast, steps down a tier instead of failing.
    ///     </para>
    ///     <para>
    ///         GPU-only. A non-null value on a CPU-variant node is rejected with a 400, because
    ///         <c>llama-fit-params</c> does not run on the CPU backend and the window could not be recorded in the
    ///         profile. The provider-side fallback resolver
    ///         (<c>DefaultProcessContextAllocationResolver</c>) ignores this field entirely; a host running that
    ///         resolver exposes no explore endpoint.
    ///     </para>
    /// </summary>
    // The annotation is documentation only: it publishes minimum/maximum into the OpenAPI schema and the generated
    // client's Zod validators. FastEndpoints does not run DataAnnotations, so the endpoint's inline check stays the
    // enforcing path — a hand-rolled request that skips the generated client still gets a 400.
    [System.ComponentModel.DataAnnotations.Range(MinExploreContextTokens, MaxExploreContextTokens)]
    public int? ContextTokens { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/profiles/benchmark</c>. Benchmarks the drafted profile identified by
///     <see cref="ProfileId" /> (carried in the body — never a route param — so the POST always has a body). An empty id
///     is rejected with a 400.
/// </summary>
public sealed class BenchmarkInferenceProfileRequest
{
    public required Guid ProfileId { get; init; }

    /// <summary>
    ///     Explicit operator override for material pressure already present before spawn. Defaults to false. Pressure
    ///     introduced during the benchmark is never bypassed.
    /// </summary>
    public bool AllowPreSpawnVramPressure { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/profiles/freeze</c>. Freezes the Explored profile identified by <see cref="ProfileId" />
///     — gated on its most recent successful benchmark (a freeze without a justifying benchmark is rejected with a 400).
///     The id is carried in the body (never a route param) so the POST always has a body. An empty id is rejected with a
///     400.
/// </summary>
public sealed class FreezeInferenceProfileRequest
{
    public required Guid ProfileId { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/profiles/invalidate</c>. Manually demotes the profile identified by
///     <see cref="ProfileId" /> to Stale. The id is carried in the body (never a route param) so the POST always has a
///     body. An empty id is rejected with a 400.
/// </summary>
public sealed class InvalidateInferenceProfileRequest
{
    public required Guid ProfileId { get; init; }
}

/// <summary>
///     Response carrying a single inference profile view — the result of <c>POST model-fit/profiles/explore</c>,
///     <c>.../freeze</c> and <c>.../invalidate</c>. A domain rejection (cloud/missing model, freeze-gate failure) is
///     surfaced as a 400 with an error body rather than this success shape.
/// </summary>
public sealed class InferenceProfileActionResponse
{
    public required InferenceProfileViewDto Profile { get; init; }
}

/// <summary>
///     Sanitized benchmark metrics projected for transport. Carries only the measured figures — NEVER the raw
///     <c>/metrics</c> scrape (<c>RawJson</c> stays server-side) so the operator surface remains sanitized.
/// </summary>
public sealed class InferenceBenchmarkMetricsDto
{
    /// <summary>Role-specific harness that produced the metrics (<c>Chat</c>, <c>Embedding</c>, or <c>Reranker</c>).</summary>
    public string? Role { get; init; }

    /// <summary>Generation throughput in tokens/second; null when not measured.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>Prompt-processing throughput in tokens/second; null when not measured.</summary>
    public double? PpTokensPerSecond { get; init; }

    /// <summary>Time-to-first-token in milliseconds; null when not measured.</summary>
    public double? TtftMs { get; init; }

    /// <summary>Total wall-clock of the whole transcript in milliseconds; null when not measured.</summary>
    public double? TotalLatencyMs { get; init; }

    /// <summary>Prompt-token reuse ratio (cold vs warm), 0..1; null when not measured.</summary>
    public double? CacheHitRate { get; init; }

    /// <summary>Wall-clock of the tool-call round in milliseconds; null when not measured.</summary>
    public double? ToolLoopMs { get; init; }

    /// <summary>Role items processed per second (texts for embedding, pairs for reranker); null for chat.</summary>
    public double? ItemsPerSecond { get; init; }

    /// <summary>Input tokens processed per wall-clock second; null when the server counters were unavailable.</summary>
    public double? InputTokensPerSecond { get; init; }

    /// <summary>Median request latency across repeated role-specific runs.</summary>
    public double? P50LatencyMs { get; init; }

    /// <summary>95th percentile request latency across repeated role-specific runs.</summary>
    public double? P95LatencyMs { get; init; }

    /// <summary>Number of texts/pairs in each role-specific request batch.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Embedding vector dimension; null for chat and reranker.</summary>
    public int? OutputDimension { get; init; }

    /// <summary>Whether all embedding values/reranker scores were finite.</summary>
    public bool? ValuesFinite { get; init; }

    /// <summary>Whether repeated vectors or reranker scores/order stayed within the deterministic tolerance.</summary>
    public bool? DeterministicOutput { get; init; }

    /// <summary>Effective free VRAM (global-free when available, otherwise process budget) observed at load.</summary>
    public long? VramLoadBytes { get; init; }

    /// <summary>Effective free VRAM (global-free when available, otherwise process budget) observed after the loop.</summary>
    public long? VramAfterBytes { get; init; }

    /// <summary>System-wide NVIDIA free VRAM observed at load.</summary>
    public long? GlobalFreeVramLoadBytes { get; init; }

    /// <summary>System-wide NVIDIA free VRAM observed after the workload.</summary>
    public long? GlobalFreeVramAfterBytes { get; init; }

    /// <summary>llama.cpp process-local VRAM budget observed at load.</summary>
    public long? ProcessBudgetVramLoadBytes { get; init; }

    /// <summary>llama.cpp process-local VRAM budget observed after the workload.</summary>
    public long? ProcessBudgetVramAfterBytes { get; init; }

    /// <summary>Lowest globally-free VRAM sampled across warmups and measured passes.</summary>
    public long? MinimumGlobalFreeVramBytes { get; init; }

    /// <summary>Lowest llama.cpp process budget sampled across warmups and measured passes.</summary>
    public long? MinimumProcessBudgetVramBytes { get; init; }

    /// <summary>Highest sampled working set of the transient llama-server process.</summary>
    public long? PeakProcessRamBytes { get; init; }

    /// <summary>
    ///     Largest server-reported context-token watermark observed during the run. Workload/allocation evidence — it
    ///     tracks the transcript the window created, so a larger number is not an improvement.
    /// </summary>
    public double? ContextTokensHighWatermark { get; init; }

    /// <summary>Draft tokens proposed by speculative decoding during the measured pass; null when not measured.</summary>
    public double? SpeculativeDraftTokens { get; init; }

    /// <summary>Draft tokens accepted during the measured pass; null when not measured.</summary>
    public double? SpeculativeAcceptedTokens { get; init; }

    /// <summary>Speculative verification steps during the measured pass; null when not measured.</summary>
    public double? SpeculativeVerificationSteps { get; init; }

    /// <summary>
    ///     Accepted/drafted token ratio. Null means the rate could not be computed, never a measured zero: the harness
    ///     returns null when the drafted-token delta is zero or missing, or when the accepted-token counter is
    ///     unavailable. Read <see cref="SpeculativeDraftTokens" /> and <see cref="SpeculativeAcceptedTokens" /> to tell
    ///     "nothing was drafted" apart from "the counters never arrived".
    /// </summary>
    public double? SpeculativeAcceptanceRate { get; init; }

    /// <summary>Whether material process-budget/global-free divergence invalidated the benchmark as external pressure.</summary>
    public required bool ExternalPressureDetected { get; init; }

    /// <summary>Number of measured passes.</summary>
    public required int Runs { get; init; }
}

/// <summary>
///     Response for <c>POST model-fit/profiles/benchmark</c>: the measured metrics plus the snapshot they were persisted
///     under, plus the (un-frozen) profile view. A failed benchmark harness leaves the snapshot Failed and is surfaced as
///     a 400 with an error body rather than this success shape.
/// </summary>
public sealed class BenchmarkInferenceProfileResponse
{
    /// <summary>The id of the persisted benchmark snapshot; null when no snapshot was created.</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>The sanitized measured metrics; null when the harness produced none.</summary>
    public InferenceBenchmarkMetricsDto? Metrics { get; init; }

    public required InferenceProfileViewDto Profile { get; init; }
}
