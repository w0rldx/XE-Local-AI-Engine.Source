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
}

// ---------------------------------------------------------------------------
// GGUF browse request/response DTOs (Hugging Face discovery passthrough)
// ---------------------------------------------------------------------------

/// <summary>
///     Query-string request for <c>GET model-fit/gguf/browse</c>. <see cref="Query" /> is a free-text repo search term
///     (null returns the popular GGUF repos); <see cref="Limit" /> bounds the result count; <see cref="Sort" /> is one of
///     <c>downloads|likes|lastModified</c> (defaults to downloads). No raw command or image reference is accepted.
/// </summary>
public sealed class BrowseGgufRepositoriesRequest
{
    public string? Query { get; init; }

    public int? Limit { get; init; }

    /// <summary>Result ordering — <c>downloads|likes|lastModified</c>; unknown/empty defaults to downloads.</summary>
    public string? Sort { get; init; }
}

/// <summary>Sanitized summary of one discovered GGUF repo (no token, no internal URL).</summary>
public sealed class GgufRepositoryResponse
{
    public required string RepoId { get; init; }

    public required bool IsGated { get; init; }

    public required long Downloads { get; init; }

    public required int Likes { get; init; }

    /// <summary>Unix-ms instant the repo was last modified.</summary>
    public required long LastModifiedAtUtc { get; init; }

    public string? License { get; init; }

    public required bool HasUsableGguf { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/browse</c>.</summary>
public sealed class BrowseGgufRepositoriesResponse
{
    public required IReadOnlyList<GgufRepositoryResponse> Items { get; init; }
}

// ---------------------------------------------------------------------------
// Download request/response DTOs (GGUF store + cancel registry)
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/download</c>. Selects a GGUF file in a repo to download. <see cref="FileName" /> picks
///     the exact <c>.gguf</c> when supplied; otherwise <see cref="Quant" /> (defaulting to the store's configured default)
///     selects the matching quant. <see cref="Revision" /> optionally pins a commit/branch. No path/token is accepted.
/// </summary>
public sealed class StartGgufDownloadRequest
{
    public required string RepoId { get; init; }

    public string? FileName { get; init; }

    public string? Quant { get; init; }

    public string? Revision { get; init; }
}

/// <summary>
///     Accepted response for <c>POST model-fit/download</c>. The download runs in the background; the response returns the
///     canonical model-name identity to track/cancel it by, and whether an existing download for the same model was
///     rejoined.
/// </summary>
public sealed class StartGgufDownloadResponse
{
    public required string ModelName { get; init; }

    public required bool AlreadyInFlight { get; init; }
}

/// <summary>Body for <c>POST model-fit/download/cancel</c>. Identifies the in-flight download by its model name.</summary>
public sealed class CancelGgufDownloadRequest
{
    public required string ModelName { get; init; }
}

/// <summary>
///     Response for <c>POST model-fit/download/cancel</c>. <see cref="Cancelled" /> is <c>true</c> when a cancellable
///     in-flight download was found and signalled; <c>false</c> when none existed (already finished / never started).
/// </summary>
public sealed class CancelGgufDownloadResponse
{
    public required string ModelName { get; init; }

    public required bool Cancelled { get; init; }
}

// ---------------------------------------------------------------------------
// Running-models / eject DTOs (llama-server supervisor passthrough)
// ---------------------------------------------------------------------------

/// <summary>One running llama-server process derived from the supervisor health snapshot. Diagnostics are sanitized.</summary>
public sealed class RunningModelResponse
{
    public required string ModelName { get; init; }

    /// <summary>Lowercase role the process serves — <c>chat|embedding</c>.</summary>
    public required string Role { get; init; }

    public required bool IsResponsive { get; init; }

    /// <summary>A sanitized, user-safe diagnostic line (no internal paths/secrets).</summary>
    public required string Detail { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/running</c>.</summary>
public sealed class ListRunningModelsResponse
{
    public required IReadOnlyList<RunningModelResponse> Items { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/running/eject</c>. Evicts (tree-kills) the running <c>(model, role)</c> process.
///     <see cref="Role" /> is <c>chat|embedding</c> (case-insensitive); an unknown role is rejected with a 400.
/// </summary>
public sealed class EjectRunningModelRequest
{
    public required string ModelName { get; init; }

    /// <summary>Role of the process to evict — <c>chat|embedding</c>. Defaults to <c>chat</c> when omitted.</summary>
    public string? Role { get; init; }
}

/// <summary>Response for <c>POST model-fit/running/eject</c>. Eviction is idempotent.</summary>
public sealed class EjectRunningModelResponse
{
    public required string ModelName { get; init; }

    public required string Role { get; init; }
}

// ---------------------------------------------------------------------------
// llama.cpp binary version DTOs (binary manager — read-only resolve + ensure)
// ---------------------------------------------------------------------------

/// <summary>
///     Response for <c>GET/POST model-fit/llamacpp/version</c>. Surfaces the resolved, hash-verified llama.cpp prebuilt
///     binary: its release tag (<see cref="Version" />), the acceleration <see cref="Variant" /> (<c>cpu|cuda|vulkan</c>),
///     whether it is the recommended pinned fallback, and the recommended pinned tag. There is no source-build / arbitrary
///     pin capability — the manager only resolves/ensures the pinned-or-selected prebuilt asset.
/// </summary>
public sealed class LlamaCppVersionResponse
{
    /// <summary>The resolved binary's llama.cpp release tag (e.g. <c>b9692</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The resolved acceleration variant, lowercased — <c>cpu|cuda|vulkan</c>.</summary>
    public required string Variant { get; init; }

    /// <summary>True when the resolved binary is the recommended pinned fallback (false when a user-selected variant).</summary>
    public required bool IsPinnedFallback { get; init; }

    /// <summary>The recommended pinned llama.cpp release tag the binary manager targets.</summary>
    public required string PinnedTag { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/llamacpp/version</c>. Ensures the prebuilt binary for the requested acceleration
///     <see cref="Variant" /> (<c>cpu|cuda|vulkan</c>, case-insensitive) is present and hash-verified, downloading it if
///     missing. There is no arbitrary version/tag input — the release tag is pinned in code. An unknown variant
///     is rejected with a 400.
/// </summary>
public sealed class EnsureLlamaCppBinaryRequest
{
    /// <summary>Acceleration variant to ensure — <c>cpu|cuda|vulkan</c>.</summary>
    public required string Variant { get; init; }
}

// ---------------------------------------------------------------------------
// HF token DTOs (Hugging Face token store — write-only value; never returned)
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/hf-token</c>. When <see cref="Token" /> is non-empty the token is stored encrypted at
///     rest; when it is null/empty the stored token is cleared (returns to anonymous access). The token is a secret: it is
///     NEVER returned by any endpoint, NEVER logged, and NEVER echoed in a response.
/// </summary>
public sealed class SetHfTokenRequest
{
    /// <summary>The Hugging Face access token to store; null/empty clears the stored token.</summary>
    public string? Token { get; init; }
}

/// <summary>
///     Response for the HF-token endpoints. Reports ONLY whether a token is currently configured — never the token value
///     itself.
/// </summary>
public sealed class HfTokenStatusResponse
{
    /// <summary>True when a token is currently stored (anonymous when false). The value itself is never exposed.</summary>
    public required bool HasToken { get; init; }
}
