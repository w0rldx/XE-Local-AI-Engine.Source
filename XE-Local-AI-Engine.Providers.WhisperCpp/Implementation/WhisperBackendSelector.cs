namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Applies the OS-aware backend-selection rule over the GPU vendor the shared <see cref="IHardwareProfiler" /> reports;
///     whisper adds no hardware probing of its own.
/// </summary>
/// <remarks>
///     NVIDIA on Windows selects <see cref="WhisperBackend.Cuda" />, the only GPU prebuilt, unless <see cref="ICudaDeviceProbe" />
///     rules a device out or <see cref="WhisperCudaFailureSignal" /> records that the pinned CUDA daemon already died; all else is
///     <see cref="WhisperBackend.Cpu" />. Linux NVIDIA gets CUDA only from the managed source build or the bring-your-own override,
///     which short-circuit the rule with their own backend; the binary manager validates their paths and bytes.
/// </remarks>
public sealed class WhisperBackendSelector : IWhisperBackendSelector
{
    private readonly ICudaDeviceProbe _cudaDeviceProbe;
    private readonly WhisperCudaFailureSignal _cudaFailureSignal;
    private int _cudaFailureWarned;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly bool _isWindows;
    private readonly ILogger<WhisperBackendSelector> _logger;
    private readonly IWhisperManagedSourceBuildSignal _managedSourceSignal;
    private readonly WhisperServerRuntimeOverrideOptions _overrideOptions;

    /// <summary>Creates a selector over the shared hardware profiler, defaulting OS detection to the live host.</summary>
    public WhisperBackendSelector(IHardwareProfiler hardwareProfiler,
        WhisperServerRuntimeOverrideOptions overrideOptions,
        IWhisperManagedSourceBuildSignal managedSourceSignal,
        ICudaDeviceProbe cudaDeviceProbe,
        WhisperCudaFailureSignal cudaFailureSignal,
        ILogger<WhisperBackendSelector>? logger = null)
        : this(hardwareProfiler, OperatingSystem.IsWindows(), overrideOptions, managedSourceSignal, cudaDeviceProbe, logger, cudaFailureSignal)
    {
    }

    /// <summary>
    ///     Test seam: pins the OS so the NVIDIA Windows/Linux split runs on any host. Omitted collaborators default to
    ///     inactive signals and a probe that always confirms a device.
    /// </summary>
    internal WhisperBackendSelector(IHardwareProfiler hardwareProfiler,
        bool isWindows,
        WhisperServerRuntimeOverrideOptions? overrideOptions = null,
        IWhisperManagedSourceBuildSignal? managedSourceSignal = null,
        ICudaDeviceProbe? cudaDeviceProbe = null,
        ILogger<WhisperBackendSelector>? logger = null,
        WhisperCudaFailureSignal? cudaFailureSignal = null)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _isWindows = isWindows;
        _overrideOptions = overrideOptions ?? new WhisperServerRuntimeOverrideOptions();
        _managedSourceSignal = managedSourceSignal ?? new WhisperManagedSourceBuildSignal();
        _cudaDeviceProbe = cudaDeviceProbe ?? new DefaultCudaDeviceProbe(isWindows: false, static () => true);
        _logger = logger ?? NullLogger<WhisperBackendSelector>.Instance;
        _cudaFailureSignal = cudaFailureSignal ?? new WhisperCudaFailureSignal();
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

        // A pinned CUDA daemon already died in this process: picking CUDA again would crash every respawn the same way.
        if (_cudaFailureSignal.Reason is { } failure)
        {
            if (Interlocked.Exchange(ref _cudaFailureWarned, value: 1) == 0)
            {
                _logger.LogWarning(
                    "{Reason}; transcription runs on the CPU backend until the node restarts. To restore GPU transcription, repair or update the NVIDIA driver and restart the node, or supply a bring-your-own whisper-server binary.",
                    failure);
            }

            return WhisperBackend.Cpu;
        }

        var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        var vendor = profile.GpuVendor;

        // The probe is consulted only where it can change the decision: Windows NVIDIA, the one CUDA prebuilt.
        var cudaDeviceAvailable = !_isWindows || vendor is not GpuVendor.Nvidia || _cudaDeviceProbe.HasEnumerableCudaDevice();
        if (!cudaDeviceAvailable)
        {
            _logger.LogWarning("An NVIDIA GPU is present but no CUDA device could be enumerated; transcription falls back to the CPU backend. "
                               + "Repair or update the NVIDIA driver to restore GPU transcription, or supply a bring-your-own whisper-server binary.");
        }

        return SelectForVendor(vendor, _isWindows, cudaDeviceAvailable);
    }

    /// <summary>Pure selection rule, exposed for direct assertion in tests.</summary>
    internal static WhisperBackend SelectForVendor(GpuVendor vendor, bool isWindows, bool cudaDeviceAvailable)
    {
        // The cuBLAS prebuilt exists for Windows x64 only, and its daemon dies on the first request where no CUDA device enumerates.
        // Linux NVIDIA lands on CPU: its CUDA lanes (managed build, bring-your-own) short-circuit above.
        return vendor == GpuVendor.Nvidia && isWindows && cudaDeviceAvailable ? WhisperBackend.Cuda : WhisperBackend.Cpu;
    }
}
