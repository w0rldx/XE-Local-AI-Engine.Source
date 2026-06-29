namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInferenceInvalidationEvaluator" />. Runs three cheap, side-effect-free checks against the
///     freeze baseline — active build tag, GPU vendor/VRAM delta, and (best-effort) live free VRAM — and returns the OR
///     of them. It is a VERDICT only: nothing here tears down a currently-running llama-server process; demotion to
///     <c>Stale</c> and the re-explore on the next cold spawn are the resolver's job.
/// </summary>
public sealed class InferenceInvalidationEvaluator : IInferenceInvalidationEvaluator
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILogger<InferenceInvalidationEvaluator> _logger;
    private readonly IAvailableVramProbe _vramProbe;

    public InferenceInvalidationEvaluator(IInstalledRuntimeStore installedRuntimeStore,
        IHardwareProfiler hardwareProfiler,
        IAvailableVramProbe vramProbe,
        ILogger<InferenceInvalidationEvaluator> logger)
    {
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _vramProbe = vramProbe ?? throw new ArgumentNullException(nameof(vramProbe));
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
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (HasHardwareDrifted(profile, hardware))
        {
            return true;
        }

        // (c) Live free VRAM below the frozen baseline (degrades to no-op when the probe is unwired).
        return await HasLiveFreeVramRegressedAsync(profile, ct).ConfigureAwait(false);
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

    // Live free VRAM below the frozen baseline. Degrades (skips) when the profile never recorded a baseline or the probe
    // reports "unknown". The real --list-devices probe has shipped (LlamaListDevicesVramProbe wins the registered floor),
    // so on supported backends this check runs; it only skips where that probe still reports unknown free VRAM.
    private async Task<bool> HasLiveFreeVramRegressedAsync(InferenceProfileRecord profile, CancellationToken ct)
    {
        if (profile.FreeVramAtFreezeBytes is not { } freezeBaseline)
        {
            return false;
        }

        var freeNow = await _vramProbe.TryGetFreeVramBytesAsync(profile.Backend, ct).ConfigureAwait(false);
        if (freeNow is not { } currentFree)
        {
            return false;
        }

        if (currentFree < freezeBaseline)
        {
            _logger.LogInformation("Inference profile {ProfileId} live free VRAM dropped below the frozen baseline; flagging for re-explore.", profile.Id);
            return true;
        }

        return false;
    }
}
