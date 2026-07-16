namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Persists, per GPU backend, whether the optimized launch config (quantized KV cache + flash attention) has been
///     proven unable to reach readiness on this host, so subsequent spawns skip it without re-paying a failed model load.
///     Backed by a small JSON file under the node data directory (mirrors <c>installed-runtime.json</c>); an in-memory
///     cache keeps the read off the spawn hot path.
/// </summary>
public interface ILlamaServerLaunchFallbackStore
{
    /// <summary>
    ///     Whether the optimized KV-quant + flash-attention config has been recorded as failed for
    ///     <paramref name="variant" /> (so the launch policy emits the safe config instead).
    /// </summary>
    Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Records that the optimized KV-quant + flash-attention config failed to reach readiness on
    ///     <paramref name="variant" /> — persisted so future spawns skip it. Idempotent.
    /// </summary>
    Task DisableOptimizedConfigAsync(GpuVariant variant, CancellationToken ct);
}
