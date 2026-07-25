namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     stable-diffusion.cpp acceleration backend selected for the host or managed runtime.
/// </summary>
/// <remarks>
///     The backend selects the managed build or exact prebuilt asset; actual GPU offload lives inside
///     stable-diffusion.cpp. Without a managed build, NVIDIA maps to <see cref="Cuda" /> on Windows only. Linux has no
///     CUDA prebuilt, so its hardware-selection path uses <see cref="Vulkan" /> when available, otherwise CPU.
/// </remarks>
public enum SdGpuBackend
{
    /// <summary>CPU-only build. The universal floor available on every supported OS/arch.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA build (Windows prebuilt with cudart pairing, or Linux managed/source override).</summary>
    Cuda = 1,

    /// <summary>Vulkan build for AMD/Intel GPUs (and the Linux fallback for NVIDIA).</summary>
    Vulkan = 2
}
