namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Applies the OS-aware backend-selection rule over the GPU vendor reported by the shared, provider-neutral
///     <see cref="IHardwareProfiler" /> — the same probe the model advisor consumes, rather than duplicated vendor
///     detection.
/// </summary>
/// <remarks>
///     Pure decision logic beyond the probe calls, so the rule is fully unit-testable via <see cref="SelectForVendor" />; the rule
///     itself is stated on <see cref="ISdGpuBackendSelector" />. An active operator override or validated managed source build
///     short-circuits hardware selection with its configured backend, which is how Linux CUDA source builds remain selected despite the
///     missing prebuilt; their paths and bytes are validated by the binary manager, never here. Without either signal, a GPU vendor is
///     served a GPU backend only where a probe confirms that backend's device.
/// </remarks>
public sealed class SdGpuBackendSelector : ISdGpuBackendSelector
{
    private readonly ICudaDeviceProbe _cudaDeviceProbe;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly bool _isWindows;
    private readonly ILogger<SdGpuBackendSelector> _logger;
    private readonly IStableDiffusionManagedSourceBuildSignal _managedSourceSignal;
    private readonly StableDiffusionServerRuntimeOverrideOptions _overrideOptions;
    private readonly IVulkanDeviceProbe _vulkanDeviceProbe;

    /// <summary>Creates a selector over the shared hardware profiler + override options + device probes, defaulting OS detection to the live host.</summary>
    public SdGpuBackendSelector(IHardwareProfiler hardwareProfiler,
        StableDiffusionServerRuntimeOverrideOptions overrideOptions,
        IVulkanDeviceProbe vulkanDeviceProbe,
        ICudaDeviceProbe cudaDeviceProbe,
        IStableDiffusionManagedSourceBuildSignal managedSourceSignal,
        ILogger<SdGpuBackendSelector>? logger = null)
        : this(hardwareProfiler, OperatingSystem.IsWindows(), vulkanDeviceProbe, cudaDeviceProbe, overrideOptions, managedSourceSignal, logger)
    {
    }

    /// <summary>
    ///     Test seam: pins the OS so the NVIDIA→CUDA/Vulkan split runs on any host, and injects faked device probes. The
    ///     override options default to an inactive instance and the CUDA probe to one that always confirms a device.
    /// </summary>
    internal SdGpuBackendSelector(IHardwareProfiler hardwareProfiler,
        bool isWindows,
        IVulkanDeviceProbe vulkanDeviceProbe,
        ICudaDeviceProbe? cudaDeviceProbe = null,
        StableDiffusionServerRuntimeOverrideOptions? overrideOptions = null,
        IStableDiffusionManagedSourceBuildSignal? managedSourceSignal = null,
        ILogger<SdGpuBackendSelector>? logger = null)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _isWindows = isWindows;
        _vulkanDeviceProbe = vulkanDeviceProbe ?? throw new ArgumentNullException(nameof(vulkanDeviceProbe));
        _cudaDeviceProbe = cudaDeviceProbe ?? new DefaultCudaDeviceProbe(isWindows: false, static () => true);
        _overrideOptions = overrideOptions ?? new StableDiffusionServerRuntimeOverrideOptions();
        _managedSourceSignal = managedSourceSignal ?? new StableDiffusionManagedSourceBuildSignal();
        _logger = logger ?? NullLogger<SdGpuBackendSelector>.Instance;
    }

    /// <inheritdoc />
    public async Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct)
    {
        // Override short-circuit: an operator-supplied binary is served as the configured backend; the vendor probe is skipped entirely (the live host may report a different/absent GPU). The path is
        // NOT validated here — the binary manager is the single path-validator.
        if (_overrideOptions.IsActive)
        {
            return _overrideOptions.Backend;
        }

        if (_managedSourceSignal.ActiveBackend is { } managedBackend)
        {
            return managedBackend;
        }

        var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        var vendor = profile.GpuVendor;

        // Each device probe is consulted only where it can change the decision. Linux GPU vendors: Vulkan is their sole GPU backend, and a Vulkan pick with no enumerable device hard-fails sd-server
        // (e.g. WSL2), whereas CPU always works. Windows NVIDIA: CUDA is the prebuilt, and a CUDA pick on a box whose driver enumerates no device exits the child within a second of every spawn.
        var vulkanDeviceAvailable = !_isWindows
                                    && vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel
                                    && _vulkanDeviceProbe.HasEnumerableVulkanDevice();

        var cudaDeviceAvailable = !_isWindows
                                  || vendor is not GpuVendor.Nvidia
                                  || _cudaDeviceProbe.HasEnumerableCudaDevice();

        if (!cudaDeviceAvailable)
        {
            _logger.LogWarning("An NVIDIA GPU is present but no CUDA device could be enumerated (the CUDA driver library is absent); the image runtime falls back to the Vulkan backend.");
        }

        return SelectForVendor(vendor, _isWindows, vulkanDeviceAvailable, cudaDeviceAvailable);
    }

    /// <summary>Pure selection rule, exposed for direct assertion in tests.</summary>
    internal static SdGpuBackend SelectForVendor(GpuVendor vendor, bool isWindows, bool vulkanDeviceAvailable, bool cudaDeviceAvailable)
    {
        return vendor switch
        {
            // NVIDIA prebuilt CUDA exists for Windows only, and only when a CUDA device actually enumerates.
            GpuVendor.Nvidia when isWindows && cudaDeviceAvailable => SdGpuBackend.Cuda,

            // Linux → Vulkan only when a Vulkan device actually enumerates, else CPU. Windows keeps the unconditional
            // Vulkan mapping, NVIDIA-without-CUDA included: every Windows GPU display driver ships a Vulkan ICD.
            GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel =>
                isWindows || vulkanDeviceAvailable ? SdGpuBackend.Vulkan : SdGpuBackend.Cpu,

            _ => SdGpuBackend.Cpu
        };
    }
}
