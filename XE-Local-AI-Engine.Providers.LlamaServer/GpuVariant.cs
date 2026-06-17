namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     llama.cpp prebuilt acceleration backend variant selected for the host.
/// </summary>
/// <remarks>
///     The variant only selects which prebuilt asset is downloaded; the actual GPU detection and offload logic lives
///     inside llama.cpp itself (epic decision #8). NVIDIA boxes map to <see cref="Cuda" /> on Windows only — llama.cpp
///     ships no prebuilt Linux CUDA asset, so a Linux NVIDIA box falls back to <see cref="Vulkan" />.
/// </remarks>
public enum GpuVariant
{
    /// <summary>CPU-only build. The universal floor available on every supported OS/arch.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA build (Windows prebuilt only; pairs with the cudart runtime archive).</summary>
    Cuda = 1,

    /// <summary>Vulkan build for AMD/Intel GPUs (and the Linux fallback for NVIDIA).</summary>
    Vulkan = 2
}
