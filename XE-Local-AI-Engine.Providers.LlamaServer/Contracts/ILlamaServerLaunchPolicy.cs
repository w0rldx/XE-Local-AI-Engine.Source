namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The single component that owns every normal-spawn launch decision: the requested context window per role, the
///     GPU KV-cache quantization and flash-attention defaults, the CPU thread policy and the persistent safe-fallback
///     state.
/// </summary>
/// <remarks>
///     Consulted by the supervisor's launch-spec builder, so these decisions are not spread across unrelated classes.
/// </remarks>
public interface ILlamaServerLaunchPolicy
{
    /// <summary>
    ///     Resolves the launch plan for a <c>(role, backend)</c> spawn.
    /// </summary>
    /// <remarks>
    ///     Precedence: a frozen-profile replay, meaning <paramref name="resolved" /> is not in explore mode, keeps its
    ///     own context, KV and flash-attention vector and the returned plan supplies only CPU threads; an explore-mode
    ///     spawn gets the role's policy context, capped to the model's train context when known, plus either the GPU
    ///     KV/FA optimization — unless a fallback was recorded for <paramref name="variant" /> — or the CPU threads.
    /// </remarks>
    Task<LlamaServerLaunchPlan> ResolveAsync(ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        ProcessContextAllocation allocation,
        CancellationToken ct);

    /// <summary>
    ///     The plan for a CPU spawn that bypasses <see cref="ResolveAsync" /> entirely: a replay or benchmark spawn
    ///     built with no policy, where the supplied frozen args ARE the experiment.
    /// </summary>
    /// <remarks>
    ///     A CPU build emits none of a GPU profile's replay args, so without this such a spawn carries neither a
    ///     context window nor thread counts and runs at llama.cpp's own defaults. It supplies exactly the two things a
    ///     CPU build can honour — the replay's own context and the CPU thread policy — never touches the KV-cache and
    ///     flash-attention vector, and requests no context for an explore-mode argument set, which pins none.
    /// </remarks>
    LlamaServerLaunchPlan ResolveCpuReplayPlan(ResolvedLaunchArguments resolved);

    /// <summary>
    ///     Records that the optimized KV-quant and flash-attention config could not reach readiness on
    ///     <paramref name="variant" /> at <paramref name="kvCacheType" />, so later <see cref="ResolveAsync" /> calls
    ///     emit the safe config for that pair.
    /// </summary>
    /// <remarks>
    ///     A failure at one KV type says nothing about the others, so the verdict is scoped to the type that actually
    ///     failed. Bookkeeping, never fatal: the caller is a safe retry that has already reached readiness, so a
    ///     verdict that cannot be persisted is logged and swallowed. A lost verdict costs one failed spawn, which
    ///     re-records it; throwing would instead kill a process that is already serving.
    /// </remarks>
    Task RecordOptimizedConfigFailedAsync(GpuVariant variant, string kvCacheType, CancellationToken ct);
}
