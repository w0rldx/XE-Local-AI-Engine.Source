namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInferenceInvalidationEvaluator" />. Runs three cheap checks against the freeze baseline —
///     active build tag, GPU vendor/VRAM delta, and live global-free VRAM. NVIDIA's refreshed global figure is preferred
    ///     because llama.cpp's process budget can ignore external WDDM pressure. Process-budget evidence remains
    ///     diagnostic and is never substituted for a global-free invalidation baseline.
/// </summary>
public sealed class InferenceInvalidationEvaluator : IInferenceInvalidationEvaluator
{
    private const long MaterialFreeVramRegressionBytes = 512L * 1024 * 1024;
    private const double MaterialFreeVramRegressionRatio = 0.05d;

    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILaunchPolicyFingerprintProvider _launchPolicyFingerprintProvider;
    private readonly ILogger<InferenceInvalidationEvaluator> _logger;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;

    public InferenceInvalidationEvaluator(IInstalledRuntimeStore installedRuntimeStore,
        IGgufModelStore ggufModelStore,
        ILaunchPolicyFingerprintProvider launchPolicyFingerprintProvider,
        IHardwareProfiler hardwareProfiler,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILogger<InferenceInvalidationEvaluator> logger)
    {
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _launchPolicyFingerprintProvider =
            launchPolicyFingerprintProvider ?? throw new ArgumentNullException(nameof(launchPolicyFingerprintProvider));
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

        // (b) The versioned launch-policy identity is authoritative. Legacy/missing fingerprints are stale rather than
        // being interpreted as equivalent to today's defaults.
        if (await HasLaunchPolicyDriftedAsync(profile, ct).ConfigureAwait(false))
        {
            return true;
        }

        // (c) Hardware delta (vendor / total-VRAM) against the freeze baseline.
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (HasHardwareDrifted(profile, hardware))
        {
            return true;
        }

        // (d) Live global-free VRAM below the frozen global-free baseline. Process budget is never substituted.
        return await HasLiveFreeVramRegressedAsync(profile, hardware, ct).ConfigureAwait(false);
    }

    private async Task<bool> HasLaunchPolicyDriftedAsync(InferenceProfileRecord profile, CancellationToken ct)
    {
        if (profile.LaunchPolicyFingerprintVersion is null || string.IsNullOrWhiteSpace(profile.LaunchPolicyFingerprint))
        {
            return true;
        }

        var path = await _ggufModelStore.ResolveModelFilePathAsync(profile.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            return !await _launchPolicyFingerprintProvider.MatchesAsync(profile, path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
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
               && profile.GlobalFreeVramAtFreezeBytes is { } freezeBaseline
               && hardware.VramBytes is { } totalVram
               && totalVram < freezeBaseline;
    }

    private async Task<bool> HasLiveFreeVramRegressedAsync(InferenceProfileRecord profile,
        HardwareProfile hardware,
        CancellationToken ct)
    {
        if (string.Equals(profile.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
            || profile.GlobalFreeVramAtFreezeBytes is not { } freezeBaseline)
        {
            return false;
        }

        var freeNow = hardware.AvailableVramBytes;
        var processBudgetNow =
            await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(profile.Backend, ct).ConfigureAwait(false);
        if (freeNow is { } globalFree && processBudgetNow is { } processBudget
            && Math.Abs(processBudget - globalFree) >= MaterialFreeVramRegressionBytes)
        {
            _logger.LogInformation(
                "Inference profile {ProfileId} observed divergent VRAM evidence: global-free {GlobalFreeBytes} bytes, process budget {ProcessBudgetBytes} bytes.",
                profile.Id,
                globalFree,
                processBudget);
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
                "Inference profile {ProfileId} global-free VRAM dropped materially below the frozen baseline; flagging for re-explore.",
                profile.Id);
            return true;
        }

        return false;
    }
}
