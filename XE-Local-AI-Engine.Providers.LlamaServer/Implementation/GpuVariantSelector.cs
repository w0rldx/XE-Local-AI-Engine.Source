namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Applies the OS-aware variant-selection rule over a detected GPU vendor. Pure decision logic — no I/O — so it
///     is fully unit-testable by faking <see cref="IGpuVendorProbe" />.
/// </summary>
/// <remarks>
///     Rule: NVIDIA → CUDA <em>only on Windows</em> (llama.cpp ships no prebuilt Linux CUDA
///     asset, so Linux NVIDIA degrades to Vulkan); AMD/Intel → Vulkan; none/unknown → CPU.
///     <para>
///         When an operator bring-your-own override is active (<see cref="LlamaServerRuntimeOverrideOptions.IsActive" />)
///         the configured variant short-circuits the vendor probe entirely. The selector keys off
///         <see cref="LlamaServerRuntimeOverrideOptions.IsActive" /> only and never validates the override path — path
///         validation is the binary manager's single responsibility.
///     </para>
/// </remarks>
public sealed class GpuVariantSelector : IGpuVariantSelector
{
    private readonly bool _isWindows;
    private readonly LlamaServerRuntimeOverrideOptions _overrideOptions;
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates a selector over the supplied vendor probe + override options, defaulting OS detection to the live host.</summary>
    public GpuVariantSelector(IGpuVendorProbe vendorProbe, LlamaServerRuntimeOverrideOptions overrideOptions)
        : this(vendorProbe, OperatingSystem.IsWindows(), overrideOptions)
    {
    }

    /// <summary>
    ///     Test seam: lets a unit test pin the OS so the NVIDIA→CUDA/Vulkan split can be exercised on any host. The
    ///     override options default to an inactive instance so existing tests keep the vendor-rule path unchanged.
    /// </summary>
    internal GpuVariantSelector(IGpuVendorProbe vendorProbe, bool isWindows, LlamaServerRuntimeOverrideOptions? overrideOptions = null)
    {
        _vendorProbe = vendorProbe ?? throw new ArgumentNullException(nameof(vendorProbe));
        _isWindows = isWindows;
        _overrideOptions = overrideOptions ?? new LlamaServerRuntimeOverrideOptions();
    }

    /// <inheritdoc />
    public async Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
    {
        // Override short-circuit: an operator-supplied binary is served as the configured variant; the vendor probe is
        // skipped entirely (the live host may report a different/absent GPU). The path is NOT validated here — the binary
        // manager is the single path-validator. No await reaches the vendor probe on this branch.
        if (_overrideOptions.IsActive)
        {
            return _overrideOptions.Variant;
        }

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
