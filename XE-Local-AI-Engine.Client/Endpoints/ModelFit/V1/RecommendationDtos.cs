namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

// ---------------------------------------------------------------------------
// Latest recommendations request/response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Query-string request for <c>GET model-fit/recommendations/latest</c>. <see cref="UseCase" /> is optional and is
///     the only cache-lookup key — the approved-image and provider-name params are gone (the advisor is the single
///     box-aware recommendation backend). It carries no raw image reference or command.
/// </summary>
public sealed class GetLatestRecommendationsRequest
{
    /// <summary>Optional use-case filter for the cached recommendation key (null matches the use-case-less snapshot).</summary>
    public string? UseCase { get; init; }
}

/// <summary>
///     Sanitized projection of one ranked recommendation row. Carries only normalized model metadata — NOTHING from the
///     snapshot's raw output, stderr excerpt or detailed diagnostics.
/// </summary>
public sealed class ModelFitRecommendationResponse
{
    public required int Rank { get; init; }

    public required string ModelName { get; init; }

    public string? ProviderModelName { get; init; }

    public required double Score { get; init; }

    public string? FitLevel { get; init; }

    public string? RunMode { get; init; }

    public string? Quantization { get; init; }

    public double? EstimatedTokensPerSecond { get; init; }

    public double? RequiredRamMb { get; init; }

    public double? RequiredVramMb { get; init; }

    public int? ContextTokens { get; init; }

    public required bool IsInstalled { get; init; }

    public string? PullModelName { get; init; }

    /// <summary>The model's release date (ISO date string) when one is reported; null otherwise.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>
    ///     Soft publisher-trust signal: <c>true</c> when the model's publisher is a known reputable GGUF packager /
    ///     first-party org. Never an exclusion gate — when <c>false</c> the UI shows a "review before downloading" warning.
    /// </summary>
    public bool IsTrustedPublisher { get; init; }

    /// <summary>
    ///     Which recommendation section this row belongs to: <c>recommended</c> / <c>canRun</c> (the curated catalog
    ///     lane, primary — locked decision D1/D2) or <c>explore</c> (the live Hugging Face discovery lane, secondary).
    ///     A pre-existing snapshot row predating the catalog lane defaults to <c>explore</c>.
    /// </summary>
    public required string Section { get; init; }

    /// <summary>The catalog entry's editorial tier (<c>S</c>/<c>A</c>/<c>B</c>), or <c>null</c> for an <c>explore</c> row.</summary>
    public string? Tier { get; init; }

    /// <summary>The catalog entry id, or <c>null</c> for an <c>explore</c> row.</summary>
    public string? CatalogId { get; init; }

    /// <summary>The catalog entry's curated display name, or <c>null</c> for an <c>explore</c> row.</summary>
    public string? CatalogDisplayName { get; init; }

    /// <summary>The catalog entry's optional user-facing note, or <c>null</c> when absent / not a catalog row.</summary>
    public string? CatalogNotes { get; init; }

    /// <summary>
    ///     <c>true</c> when this Mixture-of-Experts model was fitted with experts offloaded to system RAM
    ///     (llama.cpp <c>--n-cpu-moe</c>, locked decision D3) — the UI must label this honestly ("experts on CPU —
    ///     slower, higher quality"), never as a plain resident fit.
    /// </summary>
    public bool ExpertsOffloaded { get; init; }

    /// <summary>GPU-resident memory (GB) when <see cref="ExpertsOffloaded" />; <c>null</c> otherwise.</summary>
    public double? GpuGb { get; init; }

    /// <summary>CPU/system-RAM memory (GB) for the offloaded experts when <see cref="ExpertsOffloaded" />; <c>null</c> otherwise.</summary>
    public double? CpuGb { get; init; }

    /// <summary>
    ///     ADVISORY-ONLY quantized-KV-cache estimate for a catalog-lane row: the KV quant label the advisory was computed
    ///     at (currently always <c>Q8_0</c>), or <c>null</c> when no advisory exists (explore row, incomplete GGUF
    ///     metadata, or a snapshot predating the advisory). The row's fit/ranking/required-memory fields are ALWAYS the
    ///     fp16-KV estimate — the default chat launch uses an fp16 KV cache, so this never claims the model fits; it only
    ///     hints at the headroom a quantized KV cache could unlock on a flash-attention-capable runtime.
    /// </summary>
    public string? KvQuant { get; init; }

    /// <summary>Estimated total footprint (GB) with the quantized KV cache; <c>null</c> when <see cref="KvQuant" /> is null.</summary>
    public double? KvQuantEstimatedGb { get; init; }

    /// <summary>Scored-budget headroom (GB) with the quantized KV cache (negative = still would not fit); <c>null</c> when <see cref="KvQuant" /> is null.</summary>
    public double? KvQuantHeadroomGb { get; init; }

    /// <summary>Whether the model would fit its scored budget with the quantized KV cache; <c>null</c> when <see cref="KvQuant" /> is null.</summary>
    public bool? KvQuantFits { get; init; }

    /// <summary>Always <c>true</c> when an advisory is present — llama.cpp requires flash attention for a quantized KV cache; <c>null</c> when <see cref="KvQuant" /> is null.</summary>
    public bool? KvQuantRequiresFlashAttention { get; init; }
}

/// <summary>
///     Response for <c>GET model-fit/recommendations/latest</c>. The response is ALWAYS 200 with an explicit
///     <see cref="HasCache" /> flag rather than a 404, so the UI can distinguish "no recommendation has ever been cached"
///     (an empty/diagnostics state) from a transport error. When <see cref="HasCache" /> is <c>false</c> every snapshot
///     field is <c>null</c> and <see cref="Recommendations" /> is empty. The payload exposes only the sanitized snapshot
///     summary plus the normalized rows — never any raw output, stderr or diagnostics, and no approved-image/provider
///     coupling.
/// </summary>
public sealed class GetLatestRecommendationsResponse
{
    /// <summary>True when a cached recommendation snapshot exists for the key; false on a cache-miss (the empty state).</summary>
    public required bool HasCache { get; init; }

    public Guid? SnapshotId { get; init; }

    /// <summary>The snapshot run status string name (e.g. <c>Succeeded</c>); null on a cache-miss.</summary>
    public string? Status { get; init; }

    public string? UseCase { get; init; }

    /// <summary>Unix-ms instant the cached snapshot completed; null on a cache-miss.</summary>
    public long? LastRefreshedAtUtc { get; init; }

    public required IReadOnlyList<ModelFitRecommendationResponse> Recommendations { get; init; }
}

// ---------------------------------------------------------------------------
// Refresh request/response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/recommendations/refresh</c>. Carries the id of an existing scheduled job to fire —
///     never an image reference, command line or template id (the approved-image + provider-name params are gone). The
///     service self-guards that the job is a <c>model-recommendation-check</c> job, so this endpoint can never fire an
///     arbitrary scheduled job.
///     <para>
///         <see cref="UseCase" />, <see cref="Limit" />, <see cref="QuantOverride" /> and <see cref="CtxTarget" /> are
///         OPTIONAL per-run overrides so the manual refresh runs the currently-selected use-case / breadth / quant /
///         context instead of the definition's baked ones. Each is validated before anything fires (rejected with a 400);
///         a <c>null</c>/empty value fires the definition's stored value unchanged. No free text reaches the run.
///     </para>
/// </summary>
public sealed class RefreshRecommendationsRequest
{
    public Guid ScheduledJobId { get; init; }

    /// <summary>Optional use-case override (one of <c>general|coding|reasoning|chat|multimodal|embedding</c>); null/empty uses the baked use-case.</summary>
    public string? UseCase { get; init; }

    /// <summary>Optional recommendation breadth (<c>--limit</c>) override, validated to <c>1..50</c>; null uses the baked limit.</summary>
    public int? Limit { get; init; }

    /// <summary>Optional quant label override (e.g. <c>Q5_K_M</c>) replacing the default <c>Q4_K_M</c>; null/empty uses the baked quant.</summary>
    public string? QuantOverride { get; init; }

    /// <summary>Optional context-window target the KV-cache fit is sized against (≥256); null uses the baked context target.</summary>
    public int? CtxTarget { get; init; }
}

/// <summary>
///     Accepted response for <c>POST model-fit/recommendations/refresh</c>. The refresh is created asynchronously by the
///     scheduler (the run id is owned by the scheduler dispatcher, so it is NOT fabricated here); the response only
///     echoes the scheduled job id that was triggered.
/// </summary>
public sealed class RefreshRecommendationsResponse
{
    public required Guid ScheduledJobId { get; init; }
}

// ---------------------------------------------------------------------------
// Hardware-profile response DTO (hardware profiler passthrough)
// ---------------------------------------------------------------------------

/// <summary>
///     Sanitized projection of the node hardware profile (<c>GET model-fit/hardware-profile</c>). Carries only the
///     inference-relevant aggregates — RAM/VRAM/GPU vendor/CPU/free-disk — and never any machine identifier (hostname,
///     serial). The GPU vendor is a lowercase string (<c>nvidia|amd|intel|none|unknown</c>).
/// </summary>
public sealed class HardwareProfileResponse
{
    public required long TotalRamBytes { get; init; }

    public required long AvailableRamBytes { get; init; }

    /// <summary>Dedicated GPU VRAM in bytes, or null when it could not be measured.</summary>
    public long? VramBytes { get; init; }

    /// <summary>True only when <see cref="VramBytes" /> was actually measured.</summary>
    public required bool VramKnown { get; init; }

    /// <summary>Detected GPU vendor, lowercased (<c>nvidia|amd|intel|none|unknown</c>).</summary>
    public required string GpuVendor { get; init; }

    /// <summary>True when a usable GPU acceleration budget exists (vendor GPU present AND VRAM known).</summary>
    public required bool GpuAccelAvailable { get; init; }

    public required int CpuCores { get; init; }

    public required long FreeDiskBytes { get; init; }

    // AUD4-03 runtime device audit: whether the SELECTED inference runtime actually uses the advertised GPU or has
    // silently fallen back to the CPU. The fields above are physical facts (what hardware exists); these are runtime
    // truth (what inference will use). Non-required so a projection without an audit keeps the CPU-safe defaults.

    /// <summary>The backend inference actually uses: <c>cuda|vulkan|cpu|unknown</c>.</summary>
    public string InferenceBackend { get; init; } = "unknown";

    /// <summary>True when the host advertises a usable GPU (a vendor GPU with known VRAM).</summary>
    public bool GpuExpected { get; init; }

    /// <summary>True when a GPU is expected but the selected runtime is silently running on the CPU.</summary>
    public bool CpuFallback { get; init; }

    /// <summary>Operator-facing explanation of the CPU fallback (likely cause), or null when the GPU is being used.</summary>
    public string? CpuFallbackReason { get; init; }

    /// <summary>Operator-facing remediation (in-app paths) for the CPU fallback, or null when the GPU is being used.</summary>
    public string? CpuFallbackRemediation { get; init; }
}
