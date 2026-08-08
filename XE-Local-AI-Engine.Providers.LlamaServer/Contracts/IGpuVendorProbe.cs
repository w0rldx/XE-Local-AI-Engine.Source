namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Detects the GPU vendor present on the host. Split from <see cref="IGpuVariantSelector" /> so the
///     OS-aware variant-selection rule can be unit-tested with a faked vendor (no shelling out to
///     <c>nvidia-smi</c>/WMI/DXGI in tests).
/// </summary>
public interface IGpuVendorProbe
{
    /// <summary>Returns the detected GPU vendor, or <see cref="DetectedGpuVendor.None" /> when none/unknown.</summary>
    Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct);
}
