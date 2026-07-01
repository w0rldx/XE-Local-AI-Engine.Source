namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Minimal GPU-backend selector for the image runtime. Does <em>only enough</em> hardware probing to pick the
///     prebuilt stable-diffusion.cpp asset (CUDA vs Vulkan vs CPU). It reuses the shared, provider-neutral
///     <c>IHardwareProfiler</c> hardware probe rather than duplicating vendor detection.
/// </summary>
/// <remarks>
///     Selection rule: NVIDIA GPU → <see cref="SdGpuBackend.Cuda" /> on Windows (no prebuilt Linux CUDA asset exists);
///     on Linux, NVIDIA/AMD/Intel → <see cref="SdGpuBackend.Vulkan" /> only when an enumerable Vulkan device is confirmed
///     (via <see cref="IVulkanDeviceProbe" />), else <see cref="SdGpuBackend.Cpu" /> — a Vulkan pick with no Vulkan
///     device makes <c>sd-server</c> hard-fail (e.g. WSL2); Windows AMD/Intel → <see cref="SdGpuBackend.Vulkan" />; no
///     GPU → <see cref="SdGpuBackend.Cpu" />. An active bring-your-own override short-circuits the probe with its own
///     backend.
/// </remarks>
public interface ISdGpuBackendSelector
{
    /// <summary>Probes the host and selects the stable-diffusion.cpp prebuilt backend to download.</summary>
    Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct);
}
