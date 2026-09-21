namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Minimal GPU-variant selector probe: this provider does <em>only enough</em> hardware probing to pick the
///     prebuilt llama.cpp asset.
/// </summary>
/// <remarks>
///     The full <c>HardwareProfiler</c> — the VRAM probe and the memory-fit math — lives in the model-fit advisor and
///     is explicitly NOT built here. Selection rule: NVIDIA GPU → <see cref="GpuVariant.Cuda" /> on Windows, and
///     <see cref="GpuVariant.Vulkan" /> on Linux, where no prebuilt CUDA asset exists; AMD or Intel GPU →
///     <see cref="GpuVariant.Vulkan" />; no GPU → <see cref="GpuVariant.Cpu" />.
/// </remarks>
public interface IGpuVariantSelector
{
    /// <summary>Probes the host and selects the llama.cpp prebuilt variant to download.</summary>
    Task<GpuVariant> SelectVariantAsync(CancellationToken ct);
}
