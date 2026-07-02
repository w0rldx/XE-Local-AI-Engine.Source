namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Cheap, host-local probe answering a single question: does an enumerable Vulkan device actually exist on this box?
///     It exists because a GPU vendor being present does NOT imply Vulkan can enumerate it — most notably under WSL2,
///     where an NVIDIA GPU is exposed via CUDA/dxcore and <c>sd-server --backend vulkan0</c> hard-fails with
///     "backend 'vulkan0' was not found". The <see cref="ISdGpuBackendSelector" /> consults this before choosing Vulkan
///     so it can fall back to CPU (which always works) instead of selecting a Vulkan backend that will not start.
/// </summary>
/// <remarks>
///     The probe is intentionally conservative (fail-safe): a wrong Vulkan pick makes the image server fail to launch,
///     whereas CPU always works, so any uncertainty resolves to "absent". Implementations must be cheap and thread-safe;
///     the default computes its verdict once and caches it for the process lifetime.
/// </remarks>
public interface IVulkanDeviceProbe
{
    /// <summary><see langword="true" /> only when an enumerable Vulkan device is confirmed present; otherwise (absent or unknown) <see langword="false" />.</summary>
    bool HasEnumerableVulkanDevice();
}
