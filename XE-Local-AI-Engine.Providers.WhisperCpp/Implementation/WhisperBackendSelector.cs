namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Applies the OS-aware backend-selection rule over the GPU vendor the shared, provider-neutral
///     <see cref="IHardwareProfiler" /> reports. Reuses that probe rather than duplicating vendor detection: whisper
///     adds no hardware probing of its own to this node.
/// </summary>
/// <remarks>
///     Rule: NVIDIA on Windows selects <see cref="WhisperBackend.Cuda" />, the only GPU prebuilt upstream ships, and everything else
///     selects <see cref="WhisperBackend.Cpu" /> — including NVIDIA on Linux, where CUDA comes from the managed source build or the
///     bring-your-own override rather than a downloadable asset, and including AMD and Intel, for which whisper.cpp publishes no
///     accelerated asset at all under this backend set. An operator override or validated managed source build short-circuits the probe
///     with its own backend, which is how a Linux CUDA build stays selected; their paths and bytes are validated by the binary manager.
/// </remarks>
public sealed class WhisperBackendSelector : IWhisperBackendSelector
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly bool _isWindows;
    private readonly IWhisperManagedSourceBuildSignal _managedSourceSignal;
    private readonly WhisperServerRuntimeOverrideOptions _overrideOptions;

    /// <summary>Creates a selector over the shared hardware profiler, defaulting OS detection to the live host.</summary>
    public WhisperBackendSelector(IHardwareProfiler hardwareProfiler,
        WhisperServerRuntimeOverrideOptions overrideOptions,
        IWhisperManagedSourceBuildSignal managedSourceSignal)
        : this(hardwareProfiler, OperatingSystem.IsWindows(), overrideOptions, managedSourceSignal)
    {
    }

    /// <summary>
    ///     Test seam: pins the OS so the NVIDIA Windows/Linux split can be exercised on any host. The override and the
    ///     managed signal default to inactive instances so the vendor-rule path stays unchanged.
    /// </summary>
    internal WhisperBackendSelector(IHardwareProfiler hardwareProfiler,
        bool isWindows,
        WhisperServerRuntimeOverrideOptions? overrideOptions = null,
        IWhisperManagedSourceBuildSignal? managedSourceSignal = null)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _isWindows = isWindows;
        _overrideOptions = overrideOptions ?? new WhisperServerRuntimeOverrideOptions();
        _managedSourceSignal = managedSourceSignal ?? new WhisperManagedSourceBuildSignal();
    }

    /// <inheritdoc />
    public async Task<WhisperBackend> SelectBackendAsync(CancellationToken ct)
    {
        // An operator-supplied binary is served as ITS configured backend and the vendor probe is skipped entirely: the live host may report a different or absent GPU than the machine the binary was
        // built on. The path is not validated here — the binary manager is the single path validator.
        if (_overrideOptions.IsActive)
        {
            return _overrideOptions.Backend;
        }

        if (_managedSourceSignal.ActiveBackend is { } managedBackend)
        {
            return managedBackend;
        }

        var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        return SelectForVendor(profile.GpuVendor, _isWindows);
    }

    /// <summary>Pure selection rule, exposed for direct assertion in tests.</summary>
    internal static WhisperBackend SelectForVendor(GpuVendor vendor, bool isWindows)
    {
        // The cuBLAS prebuilt exists for Windows x64 only. Linux NVIDIA deliberately lands on CPU here: selecting CUDA would resolve bytes that upstream does not publish, and the honest CUDA lanes on
        // Linux are the managed source build and the bring-your-own override, both of which short-circuit above.
        return vendor == GpuVendor.Nvidia && isWindows ? WhisperBackend.Cuda : WhisperBackend.Cpu;
    }
}
