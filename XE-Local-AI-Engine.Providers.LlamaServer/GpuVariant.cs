namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     llama.cpp prebuilt acceleration backend variant selected for the host.
/// </summary>
/// <remarks>
///     The variant only selects which prebuilt asset is downloaded; the actual GPU detection and offload logic lives
///     inside llama.cpp itself. NVIDIA machines map to <see cref="Cuda" /> where a CUDA prebuilt is pinned (Windows x64,
///     Linux x64) and fall back to <see cref="Vulkan" /> elsewhere (arm64).
/// </remarks>
public enum GpuVariant
{
    /// <summary>CPU-only build. The universal floor available on every supported OS/arch.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA build (Windows x64 and Linux x64 prebuilts; each pairs with its cudart runtime archive).</summary>
    Cuda = 1,

    /// <summary>Vulkan build for AMD/Intel GPUs (and the arm64 fallback for NVIDIA).</summary>
    Vulkan = 2
}
