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
///     (null returns the trending GGUF repos); <see cref="Limit" /> bounds the result count; <see cref="Sort" /> is one of
///     <c>trending|downloads|likes|lastModified</c> (defaults to trending). No raw command or image reference is accepted.
/// </summary>
public sealed class BrowseGgufRepositoriesRequest
{
    public string? Query { get; init; }

    public int? Limit { get; init; }

    /// <summary>Result ordering — <c>trending|downloads|likes|lastModified</c>; unknown/empty defaults to trending.</summary>
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

    /// <summary>
    ///     Soft publisher-trust signal: <c>true</c> when the repo's publisher is a known reputable GGUF packager /
    ///     first-party org. Never an exclusion gate — every repo is still returned; when <c>false</c> the UI shows a
    ///     "review before downloading" warning.
    /// </summary>
    public bool IsTrustedPublisher { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/browse</c>.</summary>
public sealed class BrowseGgufRepositoriesResponse
{
    public required IReadOnlyList<GgufRepositoryResponse> Items { get; init; }
}

/// <summary>
///     Query-string request for <c>GET model-fit/gguf/inspect</c>. <see cref="RepoId" /> is the <c>org/name</c> repo to
///     inspect for its selectable <c>.gguf</c> files (quants + sizes). No raw command or path is accepted.
/// </summary>
public sealed class InspectGgufRepositoryRequest
{
    public string? RepoId { get; init; }
}

/// <summary>
///     Sanitized per-file row from a repo inspection: the quant a downloader selects plus its size. <see cref="IsDynamic" />
///     flags an Unsloth "Dynamic" (<c>UD-</c>) quant so the picker can badge it. No token, no internal URL, no path.
/// </summary>
public sealed class GgufRepositoryFileResponse
{
    public required string FileName { get; init; }

    public required string Quant { get; init; }

    /// <summary>Whether the quant is an Unsloth Dynamic (<c>UD-</c>) quant (e.g. <c>UD-Q4_K_XL</c>).</summary>
    public required bool IsDynamic { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>
    ///     Hardware-free quality grade for this quant (<c>NearLossless</c> / <c>SweetSpot</c> / <c>Balanced</c> /
    ///     <c>Small</c> / <c>Minimal</c>) — the <c>GgufQuantTier</c> enum name. The picker renders it as a quality hint.
    /// </summary>
    public required string QualityTier { get; init; }

    /// <summary>
    ///     How this file's size compares to the host's currently-free GPU VRAM (<c>Fits</c> / <c>Tight</c> /
    ///     <c>WontFit</c> / <c>Unknown</c>) — the <c>GgufFitVerdict</c> enum name. <c>Unknown</c> when free VRAM could
    ///     not be probed (no GPU, CPU backend, or dev box without a real probe).
    /// </summary>
    public required string FitVerdict { get; init; }

    /// <summary>Whether this is THE recommended variant for the repo. Exactly one file in a non-empty list is flagged.</summary>
    public required bool IsRecommended { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/inspect</c>: the repo id plus its selectable GGUF files.</summary>
public sealed class InspectGgufRepositoryResponse
{
    public required string RepoId { get; init; }

    public required IReadOnlyList<GgufRepositoryFileResponse> Files { get; init; }
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
// Download-status / progress-polling DTOs (IGgufDownloadCoordinator)
// ---------------------------------------------------------------------------

/// <summary>
///     Snapshot of one tracked GGUF download returned by <c>GET model-fit/gguf/downloads</c> and
///     <c>GET model-fit/gguf/downloads/{modelName}</c>. Carries only sanitized fields — no absolute path, URL, or token.
/// </summary>
public sealed class GgufDownloadStatusResponse
{
    public required string ModelName { get; init; }

    /// <summary>Phase string: <c>Running</c>, <c>Completed</c>, <c>Cancelled</c>, or <c>Failed</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Bytes received so far; <c>null</c> until the first progress event.</summary>
    public long? CompletedBytes { get; init; }

    /// <summary>Total content length in bytes; <c>null</c> when the server did not send Content-Length.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Operator-safe error reason; non-<c>null</c> only when <see cref="Phase" /> is <c>Failed</c>.</summary>
    public string? SanitizedError { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/downloads</c>.</summary>
public sealed class ListGgufDownloadsResponse
{
    public required IReadOnlyList<GgufDownloadStatusResponse> Items { get; init; }
}

/// <summary>Route-bound request for <c>GET model-fit/gguf/downloads/{modelName}</c>.</summary>
public sealed class GetGgufDownloadStatusRequest
{
    public string? ModelName { get; init; }
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
///     Response for <c>POST model-fit/llamacpp/version</c> (ensure-binary) and <c>POST model-fit/llamacpp/update</c>.
///     Surfaces the resolved, hash-verified llama.cpp prebuilt binary: its release tag (<see cref="Version" />), the
///     acceleration <see cref="Variant" /> (<c>cpu|cuda|vulkan</c>), whether it is the recommended pinned fallback, and
///     the recommended pinned tag. There is no source-build / arbitrary pin capability — the manager only resolves/ensures
///     the pinned-or-selected prebuilt asset. (The read-only GET on this route was removed: it could trigger a
///     multi-hundred-MB download on a fresh node; the runtime-status GET surfaces the installed tag+variant instead.)
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
// llama.cpp dynamic-runtime status / update DTOs (runtime updater)
// ---------------------------------------------------------------------------

/// <summary>
///     The installed llama.cpp runtime descriptor inside <see cref="LlamaCppRuntimeStatusResponse" />. Present only when
///     an <c>installed-runtime.json</c> record exists (a runtime was installed via the dynamic updater). Null on a fresh
///     node whose binary came from the pinned floor and was never recorded by an explicit install.
/// </summary>
public sealed class LlamaCppInstalledRuntimeResponse
{
    /// <summary>The installed release tag (e.g. <c>b9692</c>).</summary>
    public required string Tag { get; init; }

    /// <summary>The installed acceleration variant, lowercased — <c>cpu|cuda|vulkan</c>.</summary>
    public required string Variant { get; init; }

    /// <summary>The installed asset file name (the sentinel <c>(source-build:cuda)</c> for a managed source build — never parse it; use <see cref="IsSourceBuild" />).</summary>
    public required string Asset { get; init; }

    /// <summary>Unix-ms instant the install completed (UTC).</summary>
    public required long InstalledAtUtc { get; init; }

    /// <summary>
    ///     True when this runtime is an in-app source-built CUDA build (its source-build path is set) rather than a
    ///     downloaded prebuilt. The client uses this to label the runtime and suppress the phantom "update available".
    /// </summary>
    public required bool IsSourceBuild { get; init; }
}

/// <summary>
///     Query-string request for <c>GET model-fit/llamacpp/runtime</c>. <see cref="Refresh" /> (default false) forces a
///     fresh catalog tag-resolution (recommended + upstream-latest) — subject to the endpoint's 60s rate-limit guard.
///     Declaring it here lands the param in the OpenAPI contract so the generated client can send it (it was previously
///     read ad-hoc and absent from the schema). This still resolves tags only — never an asset — so it never downloads.
/// </summary>
public sealed class GetLlamaCppRuntimeRequest
{
    /// <summary>When true, re-checks the live release catalog (rate-limited to once per 60s); null/false serves the cached snapshot.</summary>
    public bool? Refresh { get; init; }
}

/// <summary>
///     Response for <c>GET model-fit/llamacpp/runtime</c>. Read-only: it surfaces the installed runtime (when recorded),
///     the recommended tag, the optional upstream-latest tag (resolved server-side; the client only displays it in
///     developer mode), whether a newer recommended runtime is available, whether the live catalog was offline at the
///     time of the snapshot, and how many llama.cpp model processes are currently running (the pre-update safety gate
///     reads this — a non-zero count means the runtime must not be replaced until the operator ejects them). It NEVER
///     triggers a binary download.
/// </summary>
public sealed class LlamaCppRuntimeStatusResponse
{
    /// <summary>The installed runtime descriptor, or null when no explicit install has been recorded.</summary>
    public LlamaCppInstalledRuntimeResponse? Installed { get; init; }

    /// <summary>The recommended llama.cpp release tag (the editable node setting).</summary>
    public required string RecommendedTag { get; init; }

    /// <summary>The true upstream latest tag (developer mode); null when not resolved.</summary>
    public string? UpstreamLatestTag { get; init; }

    /// <summary>True when a newer recommended runtime is resolvable and differs from the installed one.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>True when the live release catalog was unreachable/rate-limited at the time of the snapshot.</summary>
    public required bool IsOffline { get; init; }

    /// <summary>
    ///     The number of running <c>llama-server</c> processes (chat + embedding) reported by the supervisor. Counts
    ///     llama.cpp binaries only — Ollama is an opt-in external provider and is never counted. A non-zero value gates
    ///     the runtime update (the binary must not be replaced while a process holds it).
    /// </summary>
    public required int RunningProcessCount { get; init; }

    /// <summary>
    ///     True when the installed runtime is an in-app source-built CUDA build. When true the catalog-driven
    ///     "update available" is suppressed (a source build is not on the prebuilt update channel); use
    ///     <see cref="RebuildAvailable" /> instead. <c>[archMED-2]</c>
    /// </summary>
    public required bool IsSourceBuild { get; init; }

    /// <summary>
    ///     True when the installed runtime is a source build whose tag differs from the engine's current pinned tag —
    ///     i.e. a fresh in-app CUDA rebuild is available. Always false for a downloaded prebuilt. <c>[archMED-4]</c>
    /// </summary>
    public required bool RebuildAvailable { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/llamacpp/update</c>. Installs a chosen llama.cpp release <see cref="Tag" /> (validated
///     against <c>^b\d+$</c>; a malformed tag is rejected with a 400). <see cref="Variant" /> optionally overrides the
///     auto-selected acceleration variant (<c>cpu|cuda|vulkan</c>); when null the host variant is selected automatically.
/// </summary>
public sealed class UpdateLlamaCppRuntimeRequest
{
    /// <summary>The release tag to install (e.g. <c>b9700</c>); must match <c>^b\d+$</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>Optional acceleration variant override — <c>cpu|cuda|vulkan</c>; null auto-selects the host variant.</summary>
    public string? Variant { get; init; }
}

/// <summary>
///     409 Conflict body returned by <c>POST model-fit/llamacpp/update</c> when one or more <c>llama-server</c> processes
///     are still running. Replacing the runtime binary while a process holds it is unsafe, so the operator must eject all
///     running models first (the update is never auto-evicted). <see cref="RunningProcessCount" /> lets the UI explain how
///     many remain. The message is sanitized (no internal path/URL).
/// </summary>
public sealed class LlamaCppUpdateBlockedResponse
{
    /// <summary>The number of running llama.cpp processes that must be ejected before the runtime can be updated.</summary>
    public required int RunningProcessCount { get; init; }

    /// <summary>A user-safe explanation of why the update was rejected.</summary>
    public required string Message { get; init; }
}

// ---------------------------------------------------------------------------
// In-app CUDA build DTOs (ICudaBuildPrerequisiteProbe + ICudaBuildService)
// ---------------------------------------------------------------------------

/// <summary>One prerequisite checklist row for <c>GET model-fit/llamacpp/cuda-build/prerequisites</c>.</summary>
public sealed class CudaBuildPrerequisiteItemResponse
{
    /// <summary>Stable item key (e.g. <c>os-is-linux</c>, <c>nvcc</c>, <c>free-disk</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Whether this prerequisite is satisfied.</summary>
    public required bool Satisfied { get; init; }

    /// <summary>A short, sanitized note (version banner or reason) — never a path/URL/secret.</summary>
    public required string Detail { get; init; }
}

/// <summary>
///     Response for <c>GET model-fit/llamacpp/cuda-build/prerequisites</c>: the itemized toolchain checklist plus the
///     overall <see cref="CanBuild" /> gate (true only on Linux when every item is satisfied).
/// </summary>
public sealed class CudaBuildPrerequisitesResponse
{
    /// <summary>The itemized checklist, in display order.</summary>
    public required IReadOnlyList<CudaBuildPrerequisiteItemResponse> Items { get; init; }

    /// <summary>True only when the OS is Linux and every item is satisfied.</summary>
    public required bool CanBuild { get; init; }
}

/// <summary>
///     The current in-app CUDA build status (shared by <c>GET …/cuda-build/status</c>, <c>POST …/cuda-build/cancel</c>,
///     and the body of the start response): the coarse <see cref="Phase" />, whether a build is running/terminal, the last
///     N streamed log lines, a sanitized error on failure, and the pinned tag being built.
/// </summary>
public sealed class CudaBuildStatusResponse
{
    /// <summary>The coarse build phase name (e.g. <c>Cloning</c>, <c>Building</c>, <c>Completed</c>, <c>Failed</c>).</summary>
    public required string Phase { get; init; }

    /// <summary>True while a build is in flight.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>True once the build has finished (Completed/Cancelled/Failed).</summary>
    public required bool Terminal { get; init; }

    /// <summary>The last N streamed log lines (cache-root/HOME prefixes redacted).</summary>
    public required IReadOnlyList<string> LogLines { get; init; }

    /// <summary>A user-safe error reason when the build failed; otherwise null.</summary>
    public string? SanitizedError { get; init; }

    /// <summary>The pinned llama.cpp tag being built; null before the first build.</summary>
    public string? Tag { get; init; }
}

/// <summary>Response for <c>POST model-fit/llamacpp/cuda-build</c>: whether a new build started, plus the current status.</summary>
public sealed class StartCudaBuildResponse
{
    /// <summary>True when this request started a new build; false when one was already in flight (single-flight).</summary>
    public required bool Started { get; init; }

    /// <summary>The current build status snapshot.</summary>
    public required CudaBuildStatusResponse Status { get; init; }
}

/// <summary>
///     409 Conflict body for the CUDA build start/remove endpoints when a server-side gate rejects the request
///     (non-Linux, a missing prerequisite, low disk, running processes, or a build already in flight).
///     <see cref="Reason" /> is a stable machine code; <see cref="Message" /> is the sanitized explanation.
/// </summary>
public sealed class CudaBuildBlockedResponse
{
    /// <summary>Stable machine reason code (<c>not-linux</c>, <c>prerequisites</c>, <c>disk</c>, <c>processes-running</c>, <c>already-building</c>).</summary>
    public required string Reason { get; init; }

    /// <summary>A user-safe explanation of why the request was rejected.</summary>
    public required string Message { get; init; }

    /// <summary>The number of running llama.cpp processes that must be ejected first; null when not a process-gate rejection.</summary>
    public int? RunningProcessCount { get; init; }
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

// ---------------------------------------------------------------------------
// Inference Optimizer profile DTOs (IInferenceProfileService — operator surface)
// ---------------------------------------------------------------------------

/// <summary>
///     A node-local inference profile projected for transport (shared by the list, explore, freeze and invalidate
///     responses). It carries only the launch-arg facts and lifecycle metadata; the local-only machine key is
///     deliberately OMITTED — it must never leave the box. <see cref="Role" /> is the lowercase wire role
///     (<c>chat|embedding</c>) and <see cref="Status" /> is the lifecycle name (<c>Explored|Frozen|Stale</c>).
/// </summary>
public sealed class InferenceProfileViewDto
{
    public required Guid Id { get; init; }

    public required string ModelName { get; init; }

    /// <summary>Lowercase role the profile targets — <c>chat|embedding</c>.</summary>
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

    /// <summary>Free VRAM (bytes) observed when the profile was frozen; null until a freeze records it.</summary>
    public long? FreeVramAtFreezeBytes { get; init; }

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
///     400). <see cref="Role" /> is <c>chat|embedding</c> (case-insensitive; defaults to <c>chat</c> when omitted); an
///     unknown role is rejected with a 400.
/// </summary>
public sealed class ExploreInferenceProfileRequest
{
    public required string ModelName { get; init; }

    /// <summary>Role to explore — <c>chat|embedding</c>. Defaults to <c>chat</c> when omitted.</summary>
    public string? Role { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/profiles/benchmark</c>. Benchmarks the drafted profile identified by
///     <see cref="ProfileId" /> (carried in the body — never a route param — so the POST always has a body). An empty id
///     is rejected with a 400.
/// </summary>
public sealed class BenchmarkInferenceProfileRequest
{
    public required Guid ProfileId { get; init; }
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

    /// <summary>Free VRAM (bytes) observed at load; null when not measured.</summary>
    public long? VramLoadBytes { get; init; }

    /// <summary>Free VRAM (bytes) observed after the loop; null when not measured.</summary>
    public long? VramAfterBytes { get; init; }

    /// <summary>Number of transcript passes measured (one golden pass = 1).</summary>
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
