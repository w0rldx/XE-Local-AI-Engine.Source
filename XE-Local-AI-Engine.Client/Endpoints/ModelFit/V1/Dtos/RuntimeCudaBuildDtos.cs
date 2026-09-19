namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

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

    public string? SourceRepository { get; init; }
    public string? SourceCommit { get; init; }
    public LlamaCppSourceSelectionDto? SourceSelection { get; init; }
    public LlamaCppSourceRevisionModeDto? SourceRevisionMode { get; init; }
    public string? SourceRequestedCommit { get; init; }
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
    ///     <see cref="RebuildAvailable" /> instead.
    /// </summary>
    public required bool IsSourceBuild { get; init; }

    /// <summary>
    ///     True when the installed runtime is a source build whose tag differs from the engine's current pinned tag —
    ///     i.e. a fresh in-app CUDA rebuild is available. Always false for a downloaded prebuilt.
    /// </summary>
    public required bool RebuildAvailable { get; init; }

    /// <summary>
    ///     When the release catalog was last consulted, as unix milliseconds; <c>null</c> when NO check has ever run.
    ///     That distinction is the point: with the automatic runtime-update check turned off by the node's
    ///     external-access settings, an empty snapshot would otherwise be indistinguishable from a check that found
    ///     nothing, and read as a permanent "up to date". A manual refresh populates it.
    /// </summary>
    public long? CheckedAtUtc { get; init; }
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

/// <summary>
///     Response for <c>GET model-fit/llamacpp/acquisition</c>: the current first-run llama.cpp runtime acquisition
///     snapshot. Its fields mirror the <c>RuntimeAcquisitionStatusHubEvent</c> push payload 1:1 <b>on purpose</b> — the
///     client hydrates from this endpoint on mount and is then pushed the same shape over the acquisition hub, so both
///     paths reconcile through one type and one <see cref="Sequence" /> comparison.
/// </summary>
public sealed class RuntimeAcquisitionStatusResponse
{
    /// <summary>
    ///     Monotonic counter stamped on every status write, never reset within a process lifetime. Hydrate and push travel
    ///     different paths and race in BOTH directions, so the client drops any update whose sequence is not greater than
    ///     the one it already holds — otherwise a late-arriving hydrate would overwrite a terminal push and strand the
    ///     banner on a phase that already finished. Timestamps are not sufficient for this.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    ///     Phase string: <c>Idle</c>, <c>DetectingGpu</c>, <c>Downloading</c>, <c>Verifying</c>, <c>Extracting</c>,
    ///     <c>Completed</c>, or <c>Failed</c>. <c>Idle</c> means nothing has been attempted in this process lifetime.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>The acceleration variant being acquired (<c>Cpu|Cuda|Vulkan</c>); null before it is known.</summary>
    public string? Variant { get; init; }

    /// <summary>The llama.cpp release tag being acquired; null before it is resolved.</summary>
    public string? Tag { get; init; }

    /// <summary>Bytes written so far during <c>Downloading</c>; null in every other phase.</summary>
    public long? CompletedBytes { get; init; }

    /// <summary>Total download size when the response carried a <c>Content-Length</c>; null while unknown.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>1-based index of the archive being acquired (the Windows-CUDA path fetches two back to back).</summary>
    public required int StepIndex { get; init; }

    /// <summary>How many archives this acquisition fetches in total (1, or 2 for Windows CUDA).</summary>
    public required int StepCount { get; init; }

    /// <summary>Operator-safe reason the runtime could not be acquired; non-null only when <see cref="Phase" /> is <c>Failed</c>.</summary>
    public string? SanitizedError { get; init; }
}
