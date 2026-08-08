namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Applies the OS-aware variant-selection rule over a detected GPU vendor. Pure decision logic — no store I/O on the
///     hot path — so it is fully unit-testable by faking <see cref="IGpuVendorProbe" /> and the cached signal.
/// </summary>
/// <remarks>
///     Rule: NVIDIA → CUDA on Windows; on Linux NVIDIA → CUDA <em>only when a managed source build is signalled</em>
///     (<see cref="ICudaManagedBuildSignal" />), else Vulkan (llama.cpp ships no prebuilt Linux CUDA asset); AMD/Intel →
///     Vulkan; none/unknown → CPU.
///     <para>
///         When an operator bring-your-own override is active (<see cref="LlamaServerRuntimeOverrideOptions.IsActive" />)
///         the configured variant short-circuits the vendor probe entirely. The selector keys off
///         <see cref="LlamaServerRuntimeOverrideOptions.IsActive" /> only and never validates the override path — path
///         validation is the binary manager's single responsibility.
///     </para>
///     <para>
///         The managed-CUDA decision reads a CACHED flag (set on adopt, cleared on remove, seeded at startup) rather than
///         a per-call <see cref="IInstalledRuntimeStore" /> read, so selection stays cheap. Disk-presence/perms/SHA
///         validity is enforced authoritatively by the binary manager at every serve; a stale flag self-heals there.
///     </para>
/// </remarks>
public sealed class GpuVariantSelector : IGpuVariantSelector
{
    private readonly bool _isWindows;
    private readonly ICudaManagedBuildSignal _managedCudaSignal;
    private readonly LlamaServerRuntimeOverrideOptions _overrideOptions;
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates a selector over the supplied vendor probe + override options + managed-CUDA signal, defaulting OS detection to the live host.</summary>
    public GpuVariantSelector(IGpuVendorProbe vendorProbe, LlamaServerRuntimeOverrideOptions overrideOptions, ICudaManagedBuildSignal managedCudaSignal)
        : this(vendorProbe, OperatingSystem.IsWindows(), overrideOptions, managedCudaSignal)
    {
    }

    /// <summary>
    ///     Test seam: lets a unit test pin the OS so the NVIDIA→CUDA/Vulkan split can be exercised on any host. The
    ///     override options default to an inactive instance and the signal to a cleared one so existing tests keep the
    ///     vendor-rule path unchanged.
    /// </summary>
    internal GpuVariantSelector(IGpuVendorProbe vendorProbe, bool isWindows, LlamaServerRuntimeOverrideOptions? overrideOptions = null, ICudaManagedBuildSignal? managedCudaSignal = null)
    {
        _vendorProbe = vendorProbe ?? throw new ArgumentNullException(nameof(vendorProbe));
        _isWindows = isWindows;
        _overrideOptions = overrideOptions ?? new LlamaServerRuntimeOverrideOptions();
        _managedCudaSignal = managedCudaSignal ?? new CudaManagedBuildSignal();
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

        if (_managedCudaSignal.ActiveVariant is { } activeVariant)
        {
            return activeVariant;
        }

        var vendor = await _vendorProbe.DetectVendorAsync(ct).ConfigureAwait(false);

        // Managed source-built CUDA: a Linux NVIDIA box with a recorded build serves CUDA instead of the Vulkan fallback.
        // Reads the cached signal only (no per-call store read). [archHIGH-2]
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
