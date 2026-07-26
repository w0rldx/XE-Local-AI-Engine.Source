namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInferenceInvalidationEvaluator" />. Runs three cheap checks against the freeze baseline —
///     active build tag, GPU vendor/VRAM delta, and live global-free VRAM. NVIDIA's refreshed global figure is preferred
///     because llama.cpp's process budget can ignore external WDDM pressure; the vendor-agnostic process-budget probe is
///     used only when the global figure is unavailable.
/// </summary>
public sealed class InferenceInvalidationEvaluator : IInferenceInvalidationEvaluator
{
    private const long MaterialFreeVramRegressionBytes = 512L * 1024 * 1024;
    private const double MaterialFreeVramRegressionRatio = 0.05d;

    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILogger<InferenceInvalidationEvaluator> _logger;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;

    public InferenceInvalidationEvaluator(IInstalledRuntimeStore installedRuntimeStore,
        IHardwareProfiler hardwareProfiler,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILogger<InferenceInvalidationEvaluator> logger)
    {
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _processVramBudgetProbe = processVramBudgetProbe ?? throw new ArgumentNullException(nameof(processVramBudgetProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> IsStaleAsync(InferenceProfileRecord profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // (a) Build drift is the cheapest and most decisive axis — check it first and short-circuit.
        if (await HasBuildDriftedAsync(profile, ct).ConfigureAwait(false))
        {
            return true;
        }

        // (b) Hardware delta (vendor / total-VRAM) against the freeze baseline.
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        if (HasHardwareDrifted(profile, hardware))
        {
            return true;
        }

        // (c) Live global-free VRAM below the frozen baseline, with process-budget fallback when global free is unknown.
        return await HasLiveFreeVramRegressedAsync(profile, hardware, ct).ConfigureAwait(false);
    }

    // The active installed runtime is the on-disk, smoke-tested tag (IInstalledRuntimeStore). When it is unknown (fresh
    // node, nothing installed yet) the build axis degrades to "no drift" rather than forcing a re-explore on missing data.
    private async Task<bool> HasBuildDriftedAsync(InferenceProfileRecord profile, CancellationToken ct)
    {
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var activeTag = installed?.Tag;
        return !string.IsNullOrWhiteSpace(activeTag)
               && !string.Equals(activeTag, profile.LlamacppBuild, StringComparison.OrdinalIgnoreCase);
    }

    // Hardware delta. The record carries only its backend token (no stored vendor), so the EXPECTED vendor is derived
    // from the backend: cuda REQUIRES NVIDIA; vulkan requires SOME usable GPU (AMD/Intel/NVIDIA-on-Linux) and so drifts
    // only when the box has dropped to no/unknown GPU. The single total-VRAM baseline the record carries is the
    // free-at-freeze figure, so a current TOTAL VRAM below that is treated as a material shrink (card swap/removal).
    private static bool HasHardwareDrifted(InferenceProfileRecord profile, HardwareProfile hardware)
    {
        if (string.Equals(profile.Backend, InferenceBackends.Cuda, StringComparison.OrdinalIgnoreCase))
        {
            if (hardware.GpuVendor != GpuVendor.Nvidia)
            {
                return true;
            }
        }
        else if (string.Equals(profile.Backend, InferenceBackends.Vulkan, StringComparison.OrdinalIgnoreCase)
                 && hardware.GpuVendor is GpuVendor.None or GpuVendor.Unknown)
        {
            return true;
        }

        return hardware.VramKnown
               && profile.FreeVramAtFreezeBytes is { } freezeBaseline
               && hardware.VramBytes is { } totalVram
               && totalVram < freezeBaseline;
    }

    private async Task<bool> HasLiveFreeVramRegressedAsync(InferenceProfileRecord profile,
        HardwareProfile hardware,
        CancellationToken ct)
    {
        if (profile.FreeVramAtFreezeBytes is not { } freezeBaseline)
        {
            return false;
        }

        var freeNow = hardware.AvailableVramBytes;
        var source = "global-free";
        if (freeNow is null)
        {
            freeNow = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(profile.Backend, ct).ConfigureAwait(false);
            source = "process-budget fallback";
        }

        if (freeNow is not { } currentFree)
        {
            return false;
        }

        var regression = freezeBaseline - currentFree;
        var materialThreshold = Math.Max(MaterialFreeVramRegressionBytes,
            (long)Math.Ceiling(freezeBaseline * MaterialFreeVramRegressionRatio));
        if (regression >= materialThreshold)
        {
            _logger.LogInformation(
                "Inference profile {ProfileId} {VramSource} VRAM dropped materially below the frozen baseline; flagging for re-explore.",
                profile.Id,
                source);
            return true;
        }

        return false;
    }
}
