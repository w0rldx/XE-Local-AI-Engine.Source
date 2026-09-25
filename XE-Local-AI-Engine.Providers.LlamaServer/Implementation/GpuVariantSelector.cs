namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Applies the OS-aware variant-selection rule over a detected GPU vendor. Pure decision logic — no store I/O on the
///     hot path — so it is fully unit-testable by faking <see cref="IGpuVendorProbe" /> and the cached signal.
/// </summary>
/// <remarks>
///     The rule: NVIDIA to CUDA on Windows; on Linux NVIDIA to CUDA only when a managed source build is signalled
///     (<see cref="ICudaManagedBuildSignal" />) and Vulkan otherwise, llama.cpp shipping no prebuilt Linux CUDA asset;
///     AMD and Intel to Vulkan; none or unknown to CPU. An active operator override short-circuits the vendor probe
///     entirely, and the override path is never validated here — that is the binary manager's single responsibility,
///     as is re-validating the cached managed-CUDA flag this reads instead of the installed-runtime store.
/// </remarks>
public sealed class GpuVariantSelector : IGpuVariantSelector
{
    private readonly IInstalledRuntimeStore? _installedRuntimeStore;
    private readonly bool _isWindows;
    private readonly ICudaManagedBuildSignal _managedCudaSignal;
    private readonly LlamaServerRuntimeOverrideOptions _overrideOptions;
    private readonly Lazy<Task> _seedFromRecord;
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates a selector over the supplied vendor probe + override options + managed-CUDA signal, defaulting OS detection to the live host.</summary>
    public GpuVariantSelector(IGpuVendorProbe vendorProbe,
        LlamaServerRuntimeOverrideOptions overrideOptions,
        ICudaManagedBuildSignal managedCudaSignal,
        IInstalledRuntimeStore installedRuntimeStore)
        : this(vendorProbe, OperatingSystem.IsWindows(), overrideOptions, managedCudaSignal, installedRuntimeStore)
    {
    }

    /// <summary>
    ///     Test seam: lets a unit test pin the OS so the NVIDIA→CUDA/Vulkan split can be exercised on any host. The
    ///     override options default to an inactive instance and the signal to a cleared one so existing tests keep the
    ///     vendor-rule path unchanged.
    /// </summary>
    internal GpuVariantSelector(IGpuVendorProbe vendorProbe,
        bool isWindows,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        ICudaManagedBuildSignal? managedCudaSignal = null,
        IInstalledRuntimeStore? installedRuntimeStore = null)
    {
        _vendorProbe = vendorProbe ?? throw new ArgumentNullException(nameof(vendorProbe));
        _isWindows = isWindows;
        _overrideOptions = overrideOptions ?? new LlamaServerRuntimeOverrideOptions();
        _managedCudaSignal = managedCudaSignal ?? new CudaManagedBuildSignal();
        _installedRuntimeStore = installedRuntimeStore;
        _seedFromRecord = new Lazy<Task>(SeedFromRecordAsync);
    }

    /// <inheritdoc />
    public async Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
    {
        // Override short-circuit: an operator-supplied binary is served as the configured variant and the vendor probe is skipped entirely, the live host possibly
        // reporting a different or absent GPU. The path is NOT validated here — the binary manager is the single path-validator — and no await reaches the probe.
        if (_overrideOptions.IsActive)
        {
            return _overrideOptions.Variant;
        }

        if (_managedCudaSignal.ActiveVariant is null && _installedRuntimeStore is not null)
        {
            await _seedFromRecord.Value.WaitAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    ///     Seeds the unset signal once from the installed-runtime record, so a caller that runs before
    ///     <see cref="CudaBuildStartupService" /> is not admitted on Vulkan and then spawned on CUDA.
    /// </summary>
    /// <remarks>
    ///     Uncancellable, so a cancelled first caller cannot leave the others unseeded. A signal write that lands during
    ///     the read is newer than the record and wins; an unreadable record leaves the signal to the serve-time validator.
    /// </remarks>
    private async Task SeedFromRecordAsync()
    {
        var versionBefore = _managedCudaSignal.Version;
        InstalledRuntimeState? installed;
        try
        {
            installed = await _installedRuntimeStore!.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Advisory seed, cached once: a faulted task would fail every later selection. The serve-time validator is the gate.
            return;
        }

        if (CudaBuildStartupService.RecordedSourceBuildVariant(installed) is { } recorded
            && _managedCudaSignal.Version == versionBefore)
        {
            _managedCudaSignal.SetActive(recorded);
        }
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
