namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The single component that owns every normal-spawn launch decision the audited defaults were missing: the requested
///     context window per role, the GPU KV-cache quantization + flash-attention defaults, the CPU thread policy, and the
///     persistent safe-fallback state. Consulted by the supervisor's launch-spec builder so these decisions are not spread
///     across unrelated classes.
/// </summary>
public interface ILlamaServerLaunchPolicy
{
    /// <summary>
    ///     Resolves the launch plan for a <c>(role, backend)</c> spawn. Precedence: a frozen-profile replay
    ///     (<paramref name="resolved" /> not in explore mode) keeps its own <c>-c</c>/KV/FA — the returned plan then
    ///     supplies only CPU threads; an explore-mode spawn gets the role's policy context (capped to
    ///     <paramref name="modelTrainContextTokens" /> when known) plus the GPU KV/FA optimization (unless a fallback was
    ///     recorded for <paramref name="variant" />) or the CPU thread policy.
    /// </summary>
    Task<LlamaServerLaunchPlan> ResolveAsync(ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        ProcessContextAllocation allocation,
        CancellationToken ct);

    /// <summary>
    ///     The plan for a CPU spawn that bypasses <see cref="ResolveAsync" /> entirely — a replay/benchmark spawn built
    ///     with no policy, where the supplied frozen args ARE the experiment. A CPU build emits none of a GPU profile's
    ///     replay args, so such a spawn otherwise emitted neither a context window nor thread counts and ran at
    ///     llama.cpp's own defaults. This supplies exactly the two things a CPU build can honour: the replay's own
    ///     <c>-c</c> and the CPU thread policy. It never touches the KV-cache/flash-attention vector, and it requests no
    ///     context for an explore-mode argument set (which pins none).
    /// </summary>
    LlamaServerLaunchPlan ResolveCpuReplayPlan(ResolvedLaunchArguments resolved);

    /// <summary>
    ///     Records that the optimized KV-quant + flash-attention config could not reach readiness on
    ///     <paramref name="variant" /> at <paramref name="kvCacheType" />, so future <see cref="ResolveAsync" /> calls
    ///     emit the safe config for that pair. A failure at one KV type says nothing about the others, so the verdict is
    ///     scoped to the type that actually failed.
    /// </summary>
    Task RecordOptimizedConfigFailedAsync(GpuVariant variant, string kvCacheType, CancellationToken ct);
}
