namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Minimal GPU-variant selector probe. This provider does <em>only enough</em> hardware probing to pick the prebuilt
///     llama.cpp asset (CUDA vs Vulkan vs CPU). The full <c>HardwareProfiler</c> (VRAM probe, memory-fit math) lives in
///     the model-fit advisor and is explicitly NOT built here.
/// </summary>
/// <remarks>
///     Selection rule: NVIDIA GPU → <see cref="GpuVariant.Cuda" /> on Windows (no prebuilt Linux CUDA asset exists,
///     so Linux NVIDIA → <see cref="GpuVariant.Vulkan" />); AMD/Intel GPU → <see cref="GpuVariant.Vulkan" />; no GPU →
///     <see cref="GpuVariant.Cpu" />.
/// </remarks>
public interface IGpuVariantSelector
{
    /// <summary>Probes the host and selects the llama.cpp prebuilt variant to download.</summary>
    Task<GpuVariant> SelectVariantAsync(CancellationToken ct);
}
