namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The operator-facing Inference Optimizer orchestrator: explore a node-local model to draft launch args, benchmark a
///     drafted profile against the fixed golden transcript, and freeze a profile once a benchmark has succeeded. Only
///     node-local GGUF models are eligible — a cloud or missing model is rejected without spawning. Registered SCOPED
///     (it composes the scoped inference-profile + model-fit stores directly).
/// </summary>
public interface IInferenceProfileService
{
    /// <summary>
    ///     Explores <paramref name="modelName" /> for <paramref name="role" />: acquires fitted args from the sibling
    ///     machine-readable <c>llama-fit-params</c> capability, spawns one auto-fit llama-server, and upserts the single
    ///     Explored profile for the key. A GPU explore whose helper/startup evidence cannot prove concrete placement fails
    ///     observably without persisting a partial profile; CPU explores retain the GGUF-context fallback because they do
    ///     not replay GPU placement. Rejects (no spawn) when the model is not a local GGUF.
    /// </summary>
    Task<ExploreResult> ExploreAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Explores with an optional request-scoped context-window override that pins the explore spawn's <c>-c</c> for
    ///     this call only. <paramref name="contextTokens" /> is <see langword="null" /> for the default hardware-tier
    ///     behaviour; a value is silently capped by the model's train ceiling, so the returned profile's
    ///     <see cref="InferenceProfileView.CtxSize" /> is the effective window. The override is GPU-only: a non-null
    ///     value on a CPU-variant node is rejected without spawning, because <c>llama-fit-params</c> does not run there
    ///     and the requested window could not be recorded in the profile. Nothing about it is persisted.
    /// </summary>
    Task<ExploreResult> ExploreAsync(string modelName, ModelRole role, int? contextTokens, CancellationToken ct);

    /// <summary>
    ///     Benchmarks the drafted profile <paramref name="profileId" />: replays its args under a metrics-enabled spawn,
    ///     runs the golden transcript, persists a benchmark snapshot + row, and marks the snapshot Succeeded/Failed. Does
    ///     NOT freeze.
    /// </summary>
    Task<BenchmarkResult> BenchmarkAsync(Guid profileId, CancellationToken ct);

    /// <summary>
    ///     Benchmarks a profile with an explicit operator pressure override. The override affects only the pre-spawn
    ///     ambient-pressure rejection; incremental pressure detected during the workload still invalidates the run.
    /// </summary>
    Task<BenchmarkResult> BenchmarkAsync(Guid profileId, bool allowPreSpawnVramPressure, CancellationToken ct);

    /// <summary>
    ///     Freezes the Explored profile <paramref name="profileId" /> — gated on its most recent successful benchmark.
    ///     Returns a failed result (never throws) when no justifying benchmark exists or the store gate rejects.
    /// </summary>
    Task<ProfileActionResult> FreezeAsync(Guid profileId, CancellationToken ct);

    /// <summary>Returns every persisted inference profile as a view (for the GET endpoint).</summary>
    Task<IReadOnlyList<InferenceProfileView>> ListProfilesAsync(CancellationToken ct);

    /// <summary>Operator-triggered manual invalidation: demotes the profile <paramref name="profileId" /> to Stale.</summary>
    Task<ProfileActionResult> InvalidateAsync(Guid profileId, CancellationToken ct);
}

/// <summary>
///     A node-local inference profile projected for transport. The local-only machine key is deliberately OMITTED (it
///     must never leave the box); <see cref="Status" /> is surfaced as its name rather than a raw enum value.
/// </summary>
public sealed record InferenceProfileView(
    Guid Id,
    string ModelName,
    int Role,
    string Backend,
    string LlamacppBuild,
    string Quant,
    int CtxSize,
    int? NGpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttn,
    long? NParams,
    bool IsMoe,
    int? ExpertCount,
    string Status,
    Guid? BenchmarkSnapshotId,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    int? LaunchPolicyFingerprintVersion = null,
    string? LaunchPolicyFingerprint = null,
    long? GlobalFreeVramAtFreezeBytes = null,
    long? ProcessBudgetVramAtFreezeBytes = null);

/// <summary>Outcome of an explore run: the drafted profile, or a sanitized reason when the model was rejected.</summary>
public sealed record ExploreResult(bool Success, string? FailureReason, InferenceProfileView? Profile, bool Skipped = false)
{
    /// <summary>A successful explore carrying the drafted profile.</summary>
    public static ExploreResult Ok(InferenceProfileView profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ExploreResult(Success: true, FailureReason: null, profile);
    }

    /// <summary>A rejected explore carrying only the sanitized <paramref name="reason" />.</summary>
    public static ExploreResult Fail(string reason)
    {
        return new ExploreResult(Success: false, reason, Profile: null);
    }

    /// <summary>
    ///     Not attempted: a warm role for the model was serving in-flight inference, so the exclusive profiling spawn
    ///     refused to evict it. Distinct from <see cref="Fail" /> — nothing was measured and nothing went wrong.
    /// </summary>
    public static ExploreResult SkippedInUse(string reason)
    {
        return new ExploreResult(Success: false, reason, Profile: null, Skipped: true);
    }
}

/// <summary>
///     Outcome of a benchmark run: the measured metrics + the snapshot they were persisted under, plus the profile view.
///     <see cref="Success" /> mirrors the harness outcome (a failed harness leaves the snapshot Failed and the profile
///     un-frozen).
/// </summary>
public sealed record BenchmarkResult(
    bool Success,
    string? FailureReason,
    Guid? SnapshotId,
    InferenceBenchmarkMetrics? Metrics,
    InferenceProfileView? Profile,
    bool Skipped = false)
{
    /// <summary>A failed benchmark carrying a sanitized reason and (when one was created) the snapshot id.</summary>
    public static BenchmarkResult Fail(string reason, Guid? snapshotId = null)
    {
        return new BenchmarkResult(Success: false, reason, snapshotId, Metrics: null, Profile: null);
    }

    /// <summary>
    ///     Not attempted: a warm role for the model was serving in-flight inference, so the exclusive profiling spawn
    ///     refused to evict it. Distinct from <see cref="Fail" /> — nothing was measured and nothing went wrong.
    /// </summary>
    public static BenchmarkResult SkippedInUse(string reason, Guid? snapshotId = null)
    {
        return new BenchmarkResult(Success: false, reason, snapshotId, Metrics: null, Profile: null, Skipped: true);
    }
}

/// <summary>Outcome of a freeze or invalidate transition: the updated profile, or a sanitized reason when it was rejected.</summary>
public sealed record ProfileActionResult(bool Success, string? FailureReason, InferenceProfileView? Profile)
{
    /// <summary>A successful transition carrying the updated profile.</summary>
    public static ProfileActionResult Ok(InferenceProfileView profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileActionResult(Success: true, FailureReason: null, profile);
    }

    /// <summary>A rejected transition carrying only the sanitized <paramref name="reason" />.</summary>
    public static ProfileActionResult Fail(string reason)
    {
        return new ProfileActionResult(Success: false, reason, Profile: null);
    }
}
