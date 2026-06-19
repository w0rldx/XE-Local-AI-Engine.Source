namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Applies the OS-aware variant-selection rule over a detected GPU vendor. Pure decision logic — no I/O — so it
///     is fully unit-testable by faking <see cref="IGpuVendorProbe" />.
/// </summary>
/// <remarks>
///     Rule: NVIDIA → CUDA <em>only on Windows</em> (llama.cpp ships no prebuilt Linux CUDA
///     asset, so Linux NVIDIA degrades to Vulkan); AMD/Intel → Vulkan; none/unknown → CPU.
/// </remarks>
public sealed class GpuVariantSelector : IGpuVariantSelector
{
    private readonly bool _isWindows;
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates a selector over the supplied vendor probe, defaulting OS detection to the live host.</summary>
    public GpuVariantSelector(IGpuVendorProbe vendorProbe)
        : this(vendorProbe, OperatingSystem.IsWindows())
    {
    }

    /// <summary>Test seam: lets a unit test pin the OS so the NVIDIA→CUDA/Vulkan split can be exercised on any host.</summary>
    internal GpuVariantSelector(IGpuVendorProbe vendorProbe, bool isWindows)
    {
        _vendorProbe = vendorProbe ?? throw new ArgumentNullException(nameof(vendorProbe));
        _isWindows = isWindows;
    }

    /// <inheritdoc />
    public async Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
    {
        var vendor = await _vendorProbe.DetectVendorAsync(ct).ConfigureAwait(false);
        return SelectForVendor(vendor, _isWindows);
    }

    /// <summary>Pure selection rule, exposed for direct assertion in tests.</summary>
    internal static GpuVariant SelectForVendor(DetectedGpuVendor vendor, bool isWindows)
    {
        return vendor switch
        {
            // NVIDIA prebuilt CUDA exists for Windows only; Linux NVIDIA falls back to Vulkan.
            DetectedGpuVendor.Nvidia => isWindows ? GpuVariant.Cuda : GpuVariant.Vulkan,
            DetectedGpuVendor.Amd or DetectedGpuVendor.Intel => GpuVariant.Vulkan,
            _ => GpuVariant.Cpu
        };
    }
}
