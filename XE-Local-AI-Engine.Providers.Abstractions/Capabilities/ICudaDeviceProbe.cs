namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Cheap, host-local probe answering a single question: can the CUDA driver API enumerate a device on this box?
/// </summary>
/// <remarks>
///     An NVIDIA GPU from <c>nvidia-smi</c> proves the display driver, not that a CUDA build finds a device. The image and
///     transcription backend selectors consult this before choosing CUDA. Conservative toward PRESENT, because a false
///     "absent" costs a healthy NVIDIA box its GPU. Cheap and thread-safe; the default caches its verdict per process.
/// </remarks>
public interface ICudaDeviceProbe
{
    /// <summary><see langword="false" /> only when the CUDA driver API is confirmed absent; otherwise (present or unknown) <see langword="true" />.</summary>
    bool HasEnumerableCudaDevice();
}
