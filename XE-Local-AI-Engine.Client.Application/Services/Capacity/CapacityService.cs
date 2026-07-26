namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Default <see cref="ICapacityService" />. The single admission gate: cloud bypass, local-same-as-running queue,
///     and the byte-budget + process-count decision with a TOCTOU-safe decide-commit under the pending-footprint
///     ledger. Reason strings are sanitized constants — no paths, model identities, or secrets leak to the caller.
/// </summary>
public sealed class CapacityService : ICapacityService
{
    // Sanitized, user-safe constants — never interpolate a model name, path, or budget figure into a caller-facing
    // reason (the calling agent's transcript is not a trusted sink for node-internal detail).
    private const string ReasonAllow = "Capacity available.";
    private const string ReasonAllowCloud = "Cloud provider selected; no local capacity required.";
    private const string ReasonQueueSameModel = "Model already running; the spawn will share that process.";
    private const string ReasonRejectFootprintUnknown = "Insufficient capacity: the model's memory footprint could not be determined.";
    private const string ReasonRejectByteBudget = "Insufficient capacity: not enough free memory for another model.";
    private const string ReasonRejectProcessCap = "Insufficient capacity: the maximum number of concurrent models is already loaded.";

    private readonly IActiveCloudChatClientFactory _cloudFactory;
    private readonly IModelFootprintProvider _footprintProvider;
    private readonly IRuntimeDeviceAudit _runtimeAudit;
    private readonly IPendingFootprintLedger _ledger;
    private readonly ILocalModelProviderResolver _localProviderResolver;
    private readonly IOllamaModelService _ollamaModelService;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public CapacityService(IActiveCloudChatClientFactory cloudFactory,
        ILocalModelProviderResolver localProviderResolver,
        IRuntimeDeviceAudit runtimeAudit,
        ILlamaServerProcessSupervisor supervisor,
        IOllamaModelService ollamaModelService,
        IModelFootprintProvider footprintProvider,
        IPendingFootprintLedger ledger)
    {
        _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
        _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));
        _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
        _footprintProvider = footprintProvider ?? throw new ArgumentNullException(nameof(footprintProvider));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <inheritdoc />
    public async Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Cloud short-circuit: when THIS model routes to cloud (the RuntimeChatClient re-selects per send by the same
        // per-request model id), the child's sends have no local byte/process cost — admit without any probe. Passing
        // modelName (rather than checking only the node-default selection) matters when the two diverge: an active
        // Codex session must not exempt a spawn that explicitly names a local model from the local capacity check,
        // and a local model must still be admitted on its own footprint even while Codex is signed in.
        if (_cloudFactory.IsCloudProviderSelected(modelName))
        {
            return new CapacityDecision(CapacityVerdict.Allow, ReasonAllowCloud, OllamaEvictionWarning: false);
        }

        var providerName = await _localProviderResolver.ResolveProviderNameForModelAsync(modelName, ct).ConfigureAwait(false);
        var isOllama = string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase);

        // AUD4-03: warm the runtime device audit OUTSIDE the decision gate. Its --list-devices probe is bounded and
        // cached, but running it under the ledger gate would serialize every capacity decision behind a one-time probe.
        // The effective profile read under the gate below then consults the cached audit (only the raw hardware profile
        // re-probes live). This is also the documented lock ordering for AUD4-06: the capacity decision never holds the
        // ledger gate while the GPU-load admission gate is acquired — that gate is taken later, inside the supervisor
        // spawn, only after DecideAsync has fully returned and released this ledger gate.
        await _runtimeAudit.GetAuditAsync(forceRefresh: false, ct).ConfigureAwait(false);

        // The decide-commit gate serializes the read-decide-reserve so two concurrent different-model spawns cannot both
        // pass on the same snapshot. Held only for this short sequence — no inference runs under it.
        using var gate = await _ledger.EnterDecisionAsync(ct).ConfigureAwait(false);

        var running = await SnapshotRunningKeysAsync(isOllama, ct).ConfigureAwait(false);

        // Already running for this (model, role): serialize on that process; no fit math, no second load.
        if (running.Contains(new RunningKey(modelName, role)))
        {
            return new CapacityDecision(CapacityVerdict.QueueSameModel, ReasonQueueSameModel, OllamaEvictionWarning: false);
        }

        var ollamaWarning = isOllama && running.Count > 0;

        // forceRefresh: an admission decision runs per model-load (rare, and already serialized under this gate), so it
        // must read a live VRAM/RAM snapshot rather than the profiler's boot-time cache — a stale free-VRAM figure would
        // defeat the resident-model accounting below. Bounded: at most one probe in flight because of the gate.
        //
        // Invariant — the forced refresh MUST run UNDER the gate, NOT before it. The free-VRAM baseline nets out every
        // resident model, so a decision has to observe the load committed by every admission that won the gate before it;
        // reading the profile before entering would let two racing decisions share a pre-load snapshot and over-admit.
        // Holding the gate across this probe is safe because the probe is now wall-clock bounded (AUD4-07): a wedged
        // nvidia-smi is killed and the profiler degrades to the cached/CPU-safe profile, so the gate hold is capped by the
        // probe timeout and can never wedge the admission path indefinitely.
        //
        // AUD4-03: this is the EFFECTIVE profile — the live raw profile force-refreshed for a fresh free-VRAM snapshot,
        // degraded to CPU-mode (VRAM unknown) when the device audit reports a silent CPU fallback. So on a GPU box whose
        // Vulkan runtime enumerates no devices, admission sizes against system RAM instead of pretending 16 GB of VRAM
        // exists. The audit was warmed above, so this call only re-probes the raw hardware profile under the gate.
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: true, ct).ConfigureAwait(false);
        var footprint = await _footprintProvider.ResolveFootprintAsync(modelName, role, profile, ct).ConfigureAwait(false);
        if (!footprint.IsKnown)
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectFootprintUnknown, ollamaWarning);
        }

        // Process-count headroom mirrors the supervisor's loaded-cap (distinct (model,role) + this new one ≤ cap).
        if (running.Count + 1 > _localProviderResolver.MaxLoadedProcesses)
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectProcessCap, ollamaWarning);
        }

        if (!FitsResourceBudget(profile, footprint.Resources))
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
        }

        // Admit: reserve the footprint so a concurrent decision sees this in-flight load. The caller releases on child exit.
        var reservation = _ledger.Reserve(footprint.Resources);
        return new CapacityDecision(CapacityVerdict.Allow, ReasonAllow, ollamaWarning, reservation);
    }

    // The free byte budget. Both modes measure a *free* baseline (which already nets out resident loaded models) and
    // subtract ONLY the ledger reservations (in-flight-not-yet-resident spawns); subtracting resident footprints again
    // would double-count. GPU mode uses AvailableVramBytes (nvidia-smi memory.free) as that baseline; CPU mode uses
    // AvailableRamBytes. See the fallback note for the degraded GPU path when free VRAM could not be measured.
    private bool FitsResourceBudget(HardwareProfile profile, ResourceFootprint footprint)
    {
        var reserved = _ledger.Reserved;
        var freeRam = profile.AvailableRamBytes - reserved.RamBytes;
        if (profile.AvailableRamBytes <= 0 || footprint.RamBytes > freeRam)
        {
            return false;
        }

        var useGpu = profile is { GpuAccelAvailable: true, VramKnown: true } && profile.VramBytes is > 0;
        if (useGpu)
        {
            // Preferred: the measured free-VRAM baseline nets out VRAM already held by the main chat model and any warm
            // sub-agent servers — none of which pass through the ledger — so subtract only the ledger reservations.
            if (profile.AvailableVramBytes is { } freeVram)
            {
                return footprint.GpuBytes <= freeVram - reserved.GpuBytes;
            }

            // Fallback (free VRAM unmeasurable — e.g. a transient nvidia-smi partial, or a non-NVIDIA GPU that still
            // reported a total): total VRAM minus the ledger only. Honest limitation — resident VRAM held outside the
            // ledger (main chat model, warm sub-agents) is INVISIBLE on this path because the supervisor health rows
            // carry no per-process byte footprint, so it can over-admit; the process-count cap is the backstop.
            // follow-up: surface per-process footprints on the health snapshot to subtract them here too.
            return profile.GpuVendor != GpuVendor.Nvidia
                   && footprint.GpuBytes <= profile.VramBytes!.Value - reserved.GpuBytes;
        }

        return footprint.GpuBytes == 0;
    }

    // The running (model, role) keys for the relevant local provider. llama.cpp: the supervisor's per-process health
    // rows. Ollama: the running-models snapshot (role is not modeled by Ollama → treat every running model as a Chat
    // process, which is the only role a sub-agent chat spawn competes with). Probe failure degrades to "nothing running".
    private async Task<IReadOnlySet<RunningKey>> SnapshotRunningKeysAsync(bool isOllama, CancellationToken ct)
    {
        try
        {
            if (isOllama)
            {
                var snapshot = await _ollamaModelService.ListRunningModelsAsync(ct).ConfigureAwait(false);
                return snapshot
                       .Select(model => new RunningKey(model.ModelName ?? model.Name ?? string.Empty, ModelRole.Chat))
                       .ToHashSet();
            }

            var health = await _supervisor.CheckHealthAsync(ct).ConfigureAwait(false);
            return health
                   .Select(process => new RunningKey(process.ModelName, process.Role))
                   .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe failure must not throw out of the gate; degrade to "nothing running" (the byte/process checks
            // still apply against the live profile, and the supervisor cap is the real load-time enforcer).
            return new HashSet<RunningKey>();
        }
    }

    // A running model identity keyed on (model, role); Ordinal so identity matches the supervisor's keying.
    private readonly record struct RunningKey(string ModelName, ModelRole Role);
}
