namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Applies the OS-aware backend-selection rule over the GPU vendor reported by the shared, provider-neutral
///     <see cref="IHardwareProfiler" />. Reuses that probe abstraction rather than duplicating vendor detection — the
///     same hardware profiler the model advisor consumes. Pure decision logic beyond the single probe call, so the rule
///     is fully unit-testable via <see cref="SelectForVendor" />.
/// </summary>
/// <remarks>
///     Rule: NVIDIA → CUDA on Windows; on Linux NVIDIA/AMD/Intel → Vulkan <em>only when an enumerable Vulkan device is
///     confirmed</em> by <see cref="IVulkanDeviceProbe" />, else CPU (stable-diffusion.cpp ships no prebuilt Linux CUDA
///     asset, and a Vulkan pick with no enumerable Vulkan device makes <c>sd-server</c> hard-fail — e.g. WSL2, where the
///     NVIDIA GPU is exposed via CUDA/dxcore, not Vulkan); Windows AMD/Intel → Vulkan; none/unknown → CPU.
///     <para>
///         follow-up: a Linux NVIDIA box will prefer an in-app/BYO source-built CUDA <c>sd-server</c> once that build
///         lane is wired for the image runtime (mirroring the llama.cpp managed-CUDA lane); until then Linux NVIDIA
///         selects Vulkan (when a Vulkan device exists) or CPU. An active bring-your-own override
///         (<see cref="StableDiffusionServerRuntimeOverrideOptions.IsActive" />) short-circuits the probe entirely with
///         its configured backend; the path is validated by the binary manager, never here.
///     </para>
/// </remarks>
public sealed class SdGpuBackendSelector : ISdGpuBackendSelector
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly bool _isWindows;
    private readonly StableDiffusionServerRuntimeOverrideOptions _overrideOptions;
    private readonly IVulkanDeviceProbe _vulkanDeviceProbe;

    /// <summary>Creates a selector over the shared hardware profiler + override options + Vulkan device probe, defaulting OS detection to the live host.</summary>
    public SdGpuBackendSelector(IHardwareProfiler hardwareProfiler, StableDiffusionServerRuntimeOverrideOptions overrideOptions, IVulkanDeviceProbe vulkanDeviceProbe)
        : this(hardwareProfiler, OperatingSystem.IsWindows(), vulkanDeviceProbe, overrideOptions)
    {
    }

    /// <summary>
    ///     Test seam: lets a unit test pin the OS so the NVIDIA→CUDA/Vulkan split can be exercised on any host, and inject
    ///     a faked Vulkan device probe. The override options default to an inactive instance so the vendor-rule path stays
    ///     unchanged.
    /// </summary>
    internal SdGpuBackendSelector(IHardwareProfiler hardwareProfiler, bool isWindows, IVulkanDeviceProbe vulkanDeviceProbe, StableDiffusionServerRuntimeOverrideOptions? overrideOptions = null)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _isWindows = isWindows;
        _vulkanDeviceProbe = vulkanDeviceProbe ?? throw new ArgumentNullException(nameof(vulkanDeviceProbe));
        _overrideOptions = overrideOptions ?? new StableDiffusionServerRuntimeOverrideOptions();
    }

    /// <inheritdoc />
    public async Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct)
    {
        // Override short-circuit: an operator-supplied binary is served as the configured backend; the vendor probe is
        // skipped entirely (the live host may report a different/absent GPU). The path is NOT validated here — the binary
        // manager is the single path-validator.
        if (_overrideOptions.IsActive)
        {
            return _overrideOptions.Backend;
        }

        var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        var vendor = profile.GpuVendor;

        // The Vulkan device probe is consulted only where it can change the decision: Linux GPU vendors, whose sole GPU
        // backend is Vulkan. Windows/macOS keep their existing mapping (Windows NVIDIA→CUDA, AMD/Intel→Vulkan), so the
        // probe is skipped there. A confirmed Vulkan device is required on Linux because a Vulkan pick with no enumerable
        // Vulkan device makes sd-server hard-fail (e.g. WSL2), whereas CPU always works.
        var vulkanDeviceAvailable = !_isWindows
            && vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel
            && _vulkanDeviceProbe.HasEnumerableVulkanDevice();

        return SelectForVendor(vendor, _isWindows, vulkanDeviceAvailable);
    }

    /// <summary>Pure selection rule, exposed for direct assertion in tests.</summary>
    internal static SdGpuBackend SelectForVendor(GpuVendor vendor, bool isWindows, bool vulkanDeviceAvailable)
    {
        return vendor switch
        {
            // NVIDIA prebuilt CUDA exists for Windows only.
            GpuVendor.Nvidia when isWindows => SdGpuBackend.Cuda,

            // Linux NVIDIA/AMD/Intel → Vulkan only when a Vulkan device actually enumerates; else CPU (fail-safe).
            // Windows AMD/Intel keep their existing unconditional Vulkan mapping.
            GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel =>
                isWindows || vulkanDeviceAvailable ? SdGpuBackend.Vulkan : SdGpuBackend.Cpu,

            _ => SdGpuBackend.Cpu
        };
    }
}
