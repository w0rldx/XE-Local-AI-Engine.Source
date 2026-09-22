namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Cheap, host-local probe answering a single question: can the CUDA driver API enumerate a device on this box?
/// </summary>
/// <remarks>
///     An NVIDIA GPU reported from <c>nvidia-smi</c> proves the display driver, not that a CUDA build finds a device: a Windows tester
///     box reported one while <c>sd-server --backend diffusion=cuda0</c> exited within a second of every spawn.
///     <see cref="ISdGpuBackendSelector" /> consults this before choosing CUDA. Conservative toward PRESENT, unlike
///     <see cref="IVulkanDeviceProbe" />, because a false "absent" costs every healthy NVIDIA box its GPU. Implementations are cheap and
///     thread-safe; the default caches its verdict for the process lifetime.
/// </remarks>
public interface ICudaDeviceProbe
{
    /// <summary><see langword="false" /> only when the CUDA driver API is confirmed absent; otherwise (present or unknown) <see langword="true" />.</summary>
    bool HasEnumerableCudaDevice();
}
