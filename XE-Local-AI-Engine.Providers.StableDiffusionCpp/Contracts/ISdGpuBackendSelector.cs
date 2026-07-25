namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Minimal GPU-backend selector for the image runtime. Does <em>only enough</em> hardware probing to pick the
///     stable-diffusion.cpp backend (CUDA vs Vulkan vs CPU) when no operator or managed-runtime selection is active. It
///     reuses the shared, provider-neutral <c>IHardwareProfiler</c> hardware probe rather than duplicating vendor
///     detection.
/// </summary>
/// <remarks>
///     Selection rule: NVIDIA GPU → <see cref="SdGpuBackend.Cuda" /> on Windows (no prebuilt Linux CUDA asset exists);
///     on Linux, NVIDIA/AMD/Intel → <see cref="SdGpuBackend.Vulkan" /> only when an enumerable Vulkan device is confirmed
///     (via <see cref="IVulkanDeviceProbe" />), else <see cref="SdGpuBackend.Cpu" /> — a Vulkan pick with no Vulkan
///     device makes <c>sd-server</c> hard-fail (e.g. WSL2); Windows AMD/Intel → <see cref="SdGpuBackend.Vulkan" />; no
///     GPU → <see cref="SdGpuBackend.Cpu" />. An active bring-your-own override or validated managed source build
///     short-circuits the probe with its selected backend.
/// </remarks>
public interface ISdGpuBackendSelector
{
    /// <summary>Returns the active runtime backend or probes the host to select an exact prebuilt backend.</summary>
    Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct);
}
