namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Minimal GPU-backend selector for the image runtime. Does <em>only enough</em> hardware probing to pick the
///     prebuilt stable-diffusion.cpp asset (CUDA vs Vulkan vs CPU). It reuses the shared, provider-neutral
///     <c>IHardwareProfiler</c> hardware probe rather than duplicating vendor detection.
/// </summary>
/// <remarks>
///     Selection rule: NVIDIA GPU → <see cref="SdGpuBackend.Cuda" /> on Windows (no prebuilt Linux CUDA asset exists, so
///     Linux NVIDIA → <see cref="SdGpuBackend.Vulkan" />); AMD/Intel GPU → <see cref="SdGpuBackend.Vulkan" />; no GPU →
///     <see cref="SdGpuBackend.Cpu" />. An active bring-your-own override short-circuits the probe with its own backend.
/// </remarks>
public interface ISdGpuBackendSelector
{
    /// <summary>Probes the host and selects the stable-diffusion.cpp prebuilt backend to download.</summary>
    Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct);
}
