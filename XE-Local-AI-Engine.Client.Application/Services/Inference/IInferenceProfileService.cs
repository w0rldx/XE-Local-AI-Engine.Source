namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The operator-facing Inference Optimizer orchestrator: explore a node-local model to draft launch args,
///     benchmark a drafted profile against the fixed golden transcript, and freeze a profile once a benchmark has
///     succeeded.
/// </summary>
/// <remarks>
///     Only node-local GGUF models are eligible — a cloud or missing model is rejected without spawning. Registered
///     SCOPED, because it composes the scoped inference-profile and model-fit stores directly.
/// </remarks>
public interface IInferenceProfileService
{
    /// <summary>
    ///     Explores <paramref name="modelName" /> for <paramref name="role" />: acquires fitted args from the sibling
    ///     machine-readable <c>llama-fit-params</c> capability, spawns one auto-fit llama-server, and upserts the
    ///     single Explored profile for the key.
    /// </summary>
    /// <remarks>
    ///     A GPU explore whose helper/startup evidence cannot prove concrete placement fails observably without
    ///     persisting a partial profile; CPU explores retain the GGUF-context fallback because they do not replay GPU
    ///     placement. Rejects, without spawning, when the model is not a local GGUF.
    /// </remarks>
    Task<ExploreResult> ExploreAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Explores with an optional request-scoped context-window override that pins the explore spawn's <c>-c</c>
    ///     for this call only; nothing about it is persisted.
    /// </summary>
    /// <param name="contextTokens">
    ///     <see langword="null" /> for the default hardware-tier behaviour; a value is silently capped by the model's
    ///     train ceiling, so <see cref="InferenceProfileView.CtxSize" /> is the effective window.
    /// </param>
    /// <remarks>
    ///     The override is GPU-only: a non-null value on a CPU-variant node is rejected without spawning, because
    ///     <c>llama-fit-params</c> does not run there and the requested window could not be recorded in the profile.
    /// </remarks>
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
public sealed class InferenceProfileView
{
    public required Guid Id { get; init; }

    public required string ModelName { get; init; }

    public required int Role { get; init; }

    public required string Backend { get; init; }

    public required string LlamacppBuild { get; init; }

    public required string Quant { get; init; }

    public required int CtxSize { get; init; }

    public required int? NGpuLayers { get; init; }

    public required string? TensorSplit { get; init; }

    public required string? OverrideTensor { get; init; }

    public required string? KvTypeK { get; init; }

    public required string? KvTypeV { get; init; }

    public required bool FlashAttn { get; init; }

    public required long? NParams { get; init; }

    public required bool IsMoe { get; init; }

    public required int? ExpertCount { get; init; }

    public required string Status { get; init; }

    public required Guid? BenchmarkSnapshotId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public int? LaunchPolicyFingerprintVersion { get; init; }

    public string? LaunchPolicyFingerprint { get; init; }

    public long? GlobalFreeVramAtFreezeBytes { get; init; }

    public long? ProcessBudgetVramAtFreezeBytes { get; init; }
}

/// <summary>Outcome of an explore run: the drafted profile, or a sanitized reason when the model was rejected.</summary>
public sealed class ExploreResult
{
    public required bool Success { get; init; }

    public required string? FailureReason { get; init; }

    public required InferenceProfileView? Profile { get; init; }

    public bool Skipped { get; init; }

    /// <summary>A successful explore carrying the drafted profile.</summary>
    public static ExploreResult Ok(InferenceProfileView profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ExploreResult { Success = true, FailureReason = null, Profile = profile };
    }

    /// <summary>A rejected explore carrying only the sanitized <paramref name="reason" />.</summary>
    public static ExploreResult Fail(string reason)
    {
        return new ExploreResult { Success = false, FailureReason = reason, Profile = null };
    }

    /// <summary>
    ///     Not attempted: a warm role for the model was serving in-flight inference, so the exclusive profiling spawn
    ///     refused to evict it. Distinct from <see cref="Fail" /> — nothing was measured and nothing went wrong.
    /// </summary>
    public static ExploreResult SkippedInUse(string reason)
    {
        return new ExploreResult { Success = false, FailureReason = reason, Profile = null, Skipped = true };
    }
}

/// <summary>
///     Outcome of a benchmark run: the measured metrics + the snapshot they were persisted under, plus the profile view.
///     <see cref="Success" /> mirrors the harness outcome (a failed harness leaves the snapshot Failed and the profile
///     un-frozen).
/// </summary>
public sealed class BenchmarkResult
{
    public required bool Success { get; init; }

    public required string? FailureReason { get; init; }

    public required Guid? SnapshotId { get; init; }

    public required InferenceBenchmarkMetrics? Metrics { get; init; }

    public required InferenceProfileView? Profile { get; init; }

    public bool Skipped { get; init; }

    /// <summary>A failed benchmark carrying a sanitized reason and (when one was created) the snapshot id.</summary>
    public static BenchmarkResult Fail(string reason, Guid? snapshotId = null)
    {
        return new BenchmarkResult { Success = false, FailureReason = reason, SnapshotId = snapshotId, Metrics = null, Profile = null };
    }

    /// <summary>
    ///     Not attempted: a warm role for the model was serving in-flight inference, so the exclusive profiling spawn
    ///     refused to evict it. Distinct from <see cref="Fail" /> — nothing was measured and nothing went wrong.
    /// </summary>
    public static BenchmarkResult SkippedInUse(string reason, Guid? snapshotId = null)
    {
        return new BenchmarkResult { Success = false, FailureReason = reason, SnapshotId = snapshotId, Metrics = null, Profile = null, Skipped = true };
    }
}

/// <summary>Outcome of a freeze or invalidate transition: the updated profile, or a sanitized reason when it was rejected.</summary>
public sealed class ProfileActionResult
{
    public required bool Success { get; init; }

    public required string? FailureReason { get; init; }

    public required InferenceProfileView? Profile { get; init; }

    /// <summary>A successful transition carrying the updated profile.</summary>
    public static ProfileActionResult Ok(InferenceProfileView profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileActionResult { Success = true, FailureReason = null, Profile = profile };
    }

    /// <summary>A rejected transition carrying only the sanitized <paramref name="reason" />.</summary>
    public static ProfileActionResult Fail(string reason)
    {
        return new ProfileActionResult { Success = false, FailureReason = reason, Profile = null };
    }
}
