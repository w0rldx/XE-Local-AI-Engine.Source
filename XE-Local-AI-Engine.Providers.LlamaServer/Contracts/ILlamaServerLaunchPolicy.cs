namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The single component that owns every normal-spawn launch decision the audited defaults were missing: the requested
///     context window per role, the GPU KV-cache quantization + flash-attention defaults, the CPU thread policy, and the
///     persistent safe-fallback state. Consulted by the supervisor's launch-spec builder so these decisions are not spread
///     across unrelated classes (AUD4-02/05/17).
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
        long? modelTrainContextTokens,
        CancellationToken ct);

    /// <summary>
    ///     Records that the optimized KV-quant + flash-attention config could not reach readiness on
    ///     <paramref name="variant" />, so future <see cref="ResolveAsync" /> calls emit the safe config for it.
    /// </summary>
    Task RecordOptimizedConfigFailedAsync(GpuVariant variant, CancellationToken ct);
}
