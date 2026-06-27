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
    ///     Explores <paramref name="modelName" /> for <paramref name="role" />: spawns one auto-fit llama-server, parses
    ///     the fitted args (falling back to the GGUF native context when the banner is unparseable) and upserts the single
    ///     Explored profile for the key. Rejects (no spawn) when the model is not a local GGUF.
    /// </summary>
    Task<ExploreResult> ExploreAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Benchmarks the drafted profile <paramref name="profileId" />: replays its args under a metrics-enabled spawn,
    ///     runs the golden transcript, persists a benchmark snapshot + row, and marks the snapshot Succeeded/Failed. Does
    ///     NOT freeze.
    /// </summary>
    Task<BenchmarkResult> BenchmarkAsync(Guid profileId, CancellationToken ct);

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
    long? FreeVramAtFreezeBytes,
    string Status,
    Guid? BenchmarkSnapshotId,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>Outcome of an explore run: the drafted profile, or a sanitized reason when the model was rejected.</summary>
public sealed record ExploreResult(bool Success, string? FailureReason, InferenceProfileView? Profile)
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
    InferenceProfileView? Profile)
{
    /// <summary>A failed benchmark carrying a sanitized reason and (when one was created) the snapshot id.</summary>
    public static BenchmarkResult Fail(string reason, Guid? snapshotId = null)
    {
        return new BenchmarkResult(Success: false, reason, snapshotId, Metrics: null, Profile: null);
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
