namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Persists, per (GPU backend, KV-cache type), whether the optimized launch config (quantized KV cache + flash
///     attention) has been proven unable to reach readiness on this host, so subsequent spawns skip it without
///     re-paying a failed model load. Backed by a small JSON file under the node data directory (mirrors
///     <c>installed-runtime.json</c>); an in-memory cache keeps the read off the spawn hot path.
/// </summary>
/// <remarks>
///     The KV type is part of the key because a <c>q4_0</c> readiness failure says nothing about <c>q8_0</c>: keying on
///     the backend alone would let one operator experiment permanently disable KV quantization for the whole backend,
///     including the type that works.
/// </remarks>
public interface ILlamaServerLaunchFallbackStore
{
    /// <summary>
    ///     Whether the optimized KV-quant + flash-attention config has been recorded as failed for
    ///     <paramref name="variant" /> at <paramref name="kvCacheType" /> (so the launch policy emits the safe config
    ///     instead). A legacy backend-wide entry written before this store was keyed by type disables NOTHING: such
    ///     entries are ignored and dropped from the file on the first read, because reading one as "every KV type on
    ///     this backend" made the node's KV-cache-type setting inert on any host that had recorded one.
    /// </summary>
    Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, string kvCacheType, CancellationToken ct);

    /// <summary>
    ///     Records that the optimized KV-quant + flash-attention config failed to reach readiness on
    ///     <paramref name="variant" /> at <paramref name="kvCacheType" /> — persisted so future spawns of that pair skip
    ///     it. Idempotent.
    /// </summary>
    Task DisableOptimizedConfigAsync(GpuVariant variant, string kvCacheType, CancellationToken ct);
}
