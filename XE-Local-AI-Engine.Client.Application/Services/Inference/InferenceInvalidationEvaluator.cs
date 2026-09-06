namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInferenceInvalidationEvaluator" />. Runs four checks against the freeze baseline — active
///     build tag, GPU vendor/VRAM delta, live global-free VRAM, and the placement axis that refuses a row whose replay
///     would contradict today's expert-offload verdict. NVIDIA's refreshed global figure is preferred
///     because llama.cpp's process budget can ignore external WDDM pressure. Cold validation deliberately does not
///     launch a process-budget probe; global-free VRAM is the only live invalidation baseline.
/// </summary>
public sealed class InferenceInvalidationEvaluator : IInferenceInvalidationEvaluator
{
    private const long MaterialFreeVramRegressionBytes = 512L * 1024 * 1024;
    private const double MaterialFreeVramRegressionRatio = 0.05d;

    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILaunchPolicyFingerprintProvider _launchPolicyFingerprintProvider;
    private readonly IProcessContextAllocationResolver _allocationResolver;
    private readonly ILogger<InferenceInvalidationEvaluator> _logger;

    public InferenceInvalidationEvaluator(IInstalledRuntimeStore installedRuntimeStore,
        IGgufModelStore ggufModelStore,
        ILaunchPolicyFingerprintProvider launchPolicyFingerprintProvider,
        IHardwareProfiler hardwareProfiler,
        IProcessContextAllocationResolver allocationResolver,
        ILogger<InferenceInvalidationEvaluator> logger)
    {
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _launchPolicyFingerprintProvider =
            launchPolicyFingerprintProvider ?? throw new ArgumentNullException(nameof(launchPolicyFingerprintProvider));
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _allocationResolver = allocationResolver ?? throw new ArgumentNullException(nameof(allocationResolver));
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

        // (c) Re-probe under HardwareProfiler's bounded timeout so the global-free verdict cannot reuse an earlier
        // cached idle snapshot. The same refreshed profile supplies both hardware drift and live global-free VRAM.
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        if (HasHardwareDrifted(profile, hardware))
        {
            return true;
        }

        // (d) Live global-free VRAM below the frozen global-free baseline. Process budget is never launched or substituted.
        if (HasLiveFreeVramRegressed(profile, hardware))
        {
            return true;
        }

        // (e) The frozen placement contradicts today's expert-offload verdict. Last because it is the only axis that
        // prices the model against the current budgets; the cheap short-circuits above have already run.
        return await ContradictsCurrentPlacementAsync(profile, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ContradictsCurrentPlacementAsync(InferenceProfileRecord profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Only a row with NO tensor override can be hiding an expert-offload decision, and only a GPU backend can have
        // made one. Both exits are the dense/resident byte-identical path: nothing is priced, nothing is probed.
        if (!string.IsNullOrWhiteSpace(profile.OverrideTensor)
            || !InferenceBackends.TryGetGpuVariant(profile.Backend, out var variant))
        {
            return false;
        }

        try
        {
            // The SAME allocation the serving spawn resolves for these very replay arguments (the resolver caches by
            // content/role/variant/args), so the verdict this reads is the verdict that spawn would launch under.
            var replay = ResolvedLaunchArguments.Replay(profile.CtxSize,
                profile.NGpuLayers,
                profile.TensorSplit,
                profile.OverrideTensor,
                profile.KvTypeK,
                profile.KvTypeV,
                profile.FlashAttn);
            var allocation = await _allocationResolver
                                   .ResolveAsync(profile.ModelName, (ModelRole)profile.Role, variant, replay, ct)
                                   .ConfigureAwait(false);
            if (allocation?.Placement != ProcessPlacementMode.ExpertOffload)
            {
                return false;
            }

            _logger.LogWarning(
                "Inference profile {ProfileId} carries no tensor override while the current memory-fit verdict places its experts in system RAM; flagging for re-explore rather than replaying it as fully resident.",
                profile.Id);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // This runs on the cold spawn path, which must never throw. An unavailable verdict degrades to "no
            // contradiction" — the same axis-skips-on-unknown-input rule the build and hardware axes already follow.
            _logger.LogWarning(exception, "Could not re-derive the current placement verdict for inference profile {ProfileId}; leaving its placement axis unjudged.", profile.Id);
            return false;
        }
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

    private bool HasLiveFreeVramRegressed(InferenceProfileRecord profile, HardwareProfile hardware)
    {
        if (string.Equals(profile.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
            || profile.GlobalFreeVramAtFreezeBytes is not { } freezeBaseline)
        {
            return false;
        }

        var freeNow = hardware.AvailableVramBytes;
        if (freeNow is not { } currentFree)
        {
            return false;
        }

        var regression = freezeBaseline - currentFree;
        var materialThreshold = Math.Max(MaterialFreeVramRegressionBytes,
            (long)Math.Ceiling(freezeBaseline * MaterialFreeVramRegressionRatio));
        if (regression >= materialThreshold)
        {
            _logger.LogInformation("Inference profile {ProfileId} global-free VRAM dropped materially below the frozen baseline; flagging for re-explore.",
                profile.Id);
            return true;
        }

        return false;
    }
}
