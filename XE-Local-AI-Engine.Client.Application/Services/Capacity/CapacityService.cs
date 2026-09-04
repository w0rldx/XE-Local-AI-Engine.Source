namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat;

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
    private const string ReasonAllowExternal = "External endpoint configured; no local capacity required.";
    private const string ReasonQueueSameModel = "Model already running; the spawn will share that process.";
    private const string ReasonRejectFootprintUnknown = "Insufficient capacity: the model's memory footprint could not be determined.";
    private const string ReasonRejectByteBudget = "Insufficient capacity: not enough free memory for another model.";
    private const string ReasonRejectProcessCap = "Insufficient capacity: the maximum number of concurrent models is already loaded.";

    private readonly IActiveCloudChatClientFactory _cloudFactory;
    private readonly IModelFootprintProvider _footprintProvider;
    private readonly IRuntimeDeviceAudit _runtimeAudit;
    private readonly IPendingFootprintLedger _ledger;
    private readonly IProcessLaunchAdmissionRegistry _launchAdmissions;
    private readonly LlamaServerExternalEndpointOptions _externalEndpoints;
    private readonly LlamaServerSupervisorOptions _supervisorOptions;
    private readonly ILocalModelProviderResolver _localProviderResolver;
    private readonly IOllamaModelService _ollamaModelService;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public CapacityService(IActiveCloudChatClientFactory cloudFactory,
        ILocalModelProviderResolver localProviderResolver,
        IRuntimeDeviceAudit runtimeAudit,
        ILlamaServerProcessSupervisor supervisor,
        IOllamaModelService ollamaModelService,
        IModelFootprintProvider footprintProvider,
        IPendingFootprintLedger ledger,
        IProcessLaunchAdmissionRegistry launchAdmissions,
        LlamaServerExternalEndpointOptions externalEndpoints,
        LlamaServerSupervisorOptions supervisorOptions)
    {
        _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
        _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));
        _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
        _footprintProvider = footprintProvider ?? throw new ArgumentNullException(nameof(footprintProvider));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _launchAdmissions = launchAdmissions ?? throw new ArgumentNullException(nameof(launchAdmissions));
        _externalEndpoints = externalEndpoints ?? throw new ArgumentNullException(nameof(externalEndpoints));
        _supervisorOptions = supervisorOptions ?? throw new ArgumentNullException(nameof(supervisorOptions));
    }

    /// <inheritdoc />
    public async Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        return await DecideAsync(new CapacityRequest(modelName, role), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelName = request.ModelName;
        var role = request.Role;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Capacity model name must be provided.", nameof(request));
        }

        if (request.RequiredContextTokens is <= 0)
        {
            throw new ArgumentException("Required context tokens must be positive when supplied.", nameof(request));
        }

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
        var isLlamaServer = string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
        if (isLlamaServer && _externalEndpoints.Resolve(modelName, role) is not null)
        {
            return new CapacityDecision(CapacityVerdict.Allow, ReasonAllowExternal, OllamaEvictionWarning: false);
        }

        // An operator-registered external OpenAI-compatible model runs entirely on someone else's hardware: the node
        // starts no process, loads no weights, and consumes neither RAM nor VRAM for it. Admitting it without a probe is
        // therefore correct, and NOT admitting it would be actively wrong — the footprint provider has no GGUF to size,
        // so the byte-budget path below would reject every such send as "footprint could not be determined".
        //
        // Both conditions are checked on purpose. The provider name is the normal route (the save path writes a
        // provider-map row per registered model). The id's ext: scheme is the backstop for the window where a row is
        // missing — a crash between the encrypted-store commit and the map sync, or a row the reconciliation pass has
        // not repaired yet — where the model would otherwise default-route to "llamacpp" and be rejected on a footprint
        // it can never have. Neither branch grants anything: capacity admission is about local resources only, and the
        // trust/egress decision for an external model is made elsewhere, from its operator-declared locality.
        if (string.Equals(providerName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
            || ExternalModelId.HasExternalScheme(modelName))
        {
            return new CapacityDecision(CapacityVerdict.Allow, ReasonAllowExternal, OllamaEvictionWarning: false);
        }

        // Warm the runtime device audit OUTSIDE the decision gate. Its --list-devices probe is bounded and
        // cached, but running it under the ledger gate would serialize every capacity decision behind a one-time probe.
        // The effective profile read under the gate below then consults the cached audit (only the raw hardware profile
        // re-probes live). This is also the documented lock ordering for the GPU-load admission gate: the capacity
        // decision never holds the ledger gate while that gate is acquired — it is taken later, inside the supervisor
        // spawn, only after DecideAsync has fully returned and released this ledger gate.
        await _runtimeAudit.GetAuditAsync(forceRefresh: false, ct).ConfigureAwait(false);

        // The decide-commit gate serializes the read-decide-reserve so two concurrent different-model spawns cannot both
        // pass on the same snapshot. Held only for this short sequence — no inference runs under it.
        using var gate = await _ledger.EnterDecisionAsync(ct).ConfigureAwait(false);

        var runningSnapshot = await SnapshotRunningKeysAsync(isOllama, ct).ConfigureAwait(false);
        var running = runningSnapshot.Keys;

        // Already running for this (model, role): serialize on that process; no fit math, no second load.
        if (running.Contains(new RunningKey(modelName, role)))
        {
            return new CapacityDecision(CapacityVerdict.QueueSameModel, ReasonQueueSameModel, OllamaEvictionWarning: false);
        }

        var ollamaWarning = isOllama && running.Count > 0;
        var launchSnapshot = isLlamaServer
            ? _launchAdmissions.Snapshot(modelName, role)
            : new ProcessLaunchAdmissionSnapshot(new HashSet<ProcessLaunchAdmissionKey>(), HasRequestedKey: false, HasGlobalBlocker: false);
        if (launchSnapshot.HasRequestedKey || launchSnapshot.HasGlobalBlocker)
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
        }

        // forceRefresh: an admission decision runs per model-load (rare, and already serialized under this gate), so it
        // must read a live VRAM/RAM snapshot rather than the profiler's boot-time cache — a stale free-VRAM figure would
        // defeat the resident-model accounting below. Bounded: at most one probe in flight because of the gate.
        //
        // Invariant — the forced refresh MUST run UNDER the gate, NOT before it. The free-VRAM baseline nets out every
        // resident model, so a decision has to observe the load committed by every admission that won the gate before it;
        // reading the profile before entering would let two racing decisions share a pre-load snapshot and over-admit.
        // Holding the gate across this probe is safe because the probe is now wall-clock bounded: a wedged
        // nvidia-smi is killed and the profiler degrades to the cached/CPU-safe profile, so the gate hold is capped by the
        // probe timeout and can never wedge the admission path indefinitely.
        //
        // This is the EFFECTIVE profile — the live raw profile force-refreshed for a fresh free-VRAM snapshot,
        // degraded to CPU-mode (VRAM unknown) when the device audit reports a silent CPU fallback. So on a GPU box whose
        // Vulkan runtime enumerates no devices, admission sizes against system RAM instead of pretending 16 GB of VRAM
        // exists. The audit was warmed above, so this call only re-probes the raw hardware profile under the gate.
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: true, ct).ConfigureAwait(false);
        var footprint = await _footprintProvider
                              .ResolveFootprintAsync(modelName, role, profile, request.RequiredContextTokens, request.KvCacheType, ct)
                              .ConfigureAwait(false);
        if (!footprint.IsKnown)
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectFootprintUnknown, ollamaWarning);
        }

        // Process-count headroom mirrors the supervisor's loaded-cap (distinct (model,role) + this new one ≤ cap).
        var activeProcessKeys = running.Select(static key => new ProcessLaunchAdmissionKey(key.ModelName, key.Role))
                                       .Concat(launchSnapshot.AdmittedKeys)
                                       .ToHashSet();
        if (activeProcessKeys.Count + 1 > _localProviderResolver.MaxLoadedProcesses)
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectProcessCap, ollamaWarning);
        }

        var hasUnmeasuredGpuLoad = !runningSnapshot.IsKnown
                                   || running.Count > 0
                                   || isLlamaServer
                                   && role == ModelRole.Chat
                                   && _supervisorOptions.Speculative.RequiresExternalDraftModel;
        while (!FitsResourceBudget(profile, footprint.Resources, hasUnmeasuredGpuLoad))
        {
            if (!_footprintProvider.TryDownTierForAdmission(footprint, out var downTiered))
            {
                return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
            }

            // A caller that NAMED a required window launches AT that window (a benchmark replays its frozen -c), so a
            // tier below it must never be admitted: the reservation would under-book the bytes the process really takes,
            // and committing it pins the model's shared allocation under the required window for the whole process
            // lifetime — every later admission naming that window then fails the required-context check and surfaces as
            // "the model's memory footprint could not be determined" until the app restarts.
            if (request.RequiredContextTokens is { } required
                && downTiered.Admission?.Allocation.ProcessContextTokens < required)
            {
                return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
            }

            footprint = downTiered;
        }

        if (!_footprintProvider.TryCommitAdmissionFootprint(footprint, out footprint)
            || !FitsResourceBudget(profile, footprint.Resources, hasUnmeasuredGpuLoad))
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
        }

        // Publish only after the exact footprint is reserved. Registry failure disposes the tentative reservation before
        // returning, preserving the ledger -> registry lock order and leaving neither half of the admission live.
        using var reservation = new AdmissionReservation(_ledger.Reserve(footprint.Resources));
        if (!isLlamaServer || !request.PublishLaunchAdmission)
        {
            return reservation.TransferToDecision(ollamaWarning);
        }

        if (footprint.Admission is null || !reservation.TryAttach(_launchAdmissions, footprint.Admission))
        {
            return new CapacityDecision(CapacityVerdict.RejectInsufficient, ReasonRejectByteBudget, ollamaWarning);
        }

        return reservation.TransferToDecision(ollamaWarning);
    }

    // Each non-zero resource axis is checked against its live free baseline, which already nets out resident loaded
    // models, then reduced only by in-flight ledger reservations. A zero axis needs no measurement: fully GPU-resident
    // llama.cpp allocations memory-map the GGUF and therefore carry no committed-RAM reservation.
    private bool FitsResourceBudget(HardwareProfile profile, ResourceFootprint footprint, bool hasUnmeasuredGpuLoad)
    {
        var reserved = _ledger.Reserved;
        if (footprint.RamBytes > 0)
        {
            var freeRam = profile.AvailableRamBytes - reserved.RamBytes;
            if (profile.AvailableRamBytes <= 0 || footprint.RamBytes > freeRam)
            {
                return false;
            }
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

            // NVIDIA has an authoritative global-free reader. If that measurement is absent, fail closed: total VRAM
            // cannot reveal residents outside the ledger and would over-admit. Other vendors currently expose only total
            // VRAM. Their degraded fallback is safe only for a first, non-external-draft launch: a known resident process
            // or a second draft GGUF lives outside the ledger, and without a free-VRAM reading there is no byte-accurate
            // value to subtract. Reject those cases rather than treating the process-count cap as memory accounting.
            if (profile.GpuVendor == GpuVendor.Nvidia)
            {
                return false;
            }

            if (hasUnmeasuredGpuLoad)
            {
                return false;
            }

            return footprint.GpuBytes <= profile.VramBytes!.Value - reserved.GpuBytes;
        }

        return footprint.GpuBytes == 0;
    }

    // The running (model, role) keys for the relevant local provider. llama.cpp: the supervisor's per-process health
    // rows. Ollama: the running-models snapshot (role is not modeled by Ollama → treat every running model as a Chat
    // process, which is the only role a sub-agent chat spawn competes with). Probe failure returns an explicit unknown
    // state: CPU/free-VRAM decisions still have authoritative byte baselines, while the non-NVIDIA total-VRAM fallback
    // must reject because it cannot establish whether unledgered residents already consume that total.
    private async Task<RunningSnapshot> SnapshotRunningKeysAsync(bool isOllama, CancellationToken ct)
    {
        try
        {
            if (isOllama)
            {
                var snapshot = await _ollamaModelService.ListRunningModelsAsync(ct).ConfigureAwait(false);
                return new RunningSnapshot(snapshot
                                           .Select(model => new RunningKey(model.ModelName ?? model.Name ?? string.Empty, ModelRole.Chat))
                                           .ToHashSet(),
                    IsKnown: true);
            }

            var health = await _supervisor.CheckHealthAsync(ct).ConfigureAwait(false);

            // EXITED entries are not running. The supervisor's table keeps a crashed process until the idle reaper
            // collects it (up to a quarter of the idle TTL), and counting a corpse as resident is wrong in both
            // directions: it burns a loaded-process slot in the headroom check below, and it short-circuits this
            // decision to QueueSameModel — telling the caller to serialize on a process that can never grant a lease.
            // That is what stranded the adaptive-effort fast-model swap after its llama-server died: every later turn
            // was refused instead of relaunching through the ordinary ensure-running path. A process that is alive but
            // merely unresponsive still holds its VRAM and its slot, so only HasExited is filtered, never IsResponsive.
            return new RunningSnapshot(health
                                       .Where(static process => !process.HasExited)
                                       .Select(process => new RunningKey(process.ModelName, process.Role))
                                       .ToHashSet(),
                IsKnown: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe failure must not throw out of the gate. Preserve the uncertainty so any decision relying on
            // total VRAM rather than a live free-VRAM/RAM baseline can fail closed instead of treating unknown as empty.
            return new RunningSnapshot(new HashSet<RunningKey>(), IsKnown: false);
        }
    }

    // A running model identity keyed on (model, role), matching the supervisor's case-insensitive process identity.
    private readonly record struct RunningKey(string ModelName, ModelRole Role)
    {
        public bool Equals(RunningKey other) =>
            Role == other.Role && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), Role);
    }

    private readonly record struct RunningSnapshot(IReadOnlySet<RunningKey> Keys, bool IsKnown);

    private sealed class AdmissionReservation(IDisposable footprintReservation) : IDisposable
    {
        private IDisposable? _reservation = footprintReservation ?? throw new ArgumentNullException(nameof(footprintReservation));

        public bool TryAttach(IProcessLaunchAdmissionRegistry registry, ProcessLaunchAdmission admission)
        {
            var launchLease = registry.Acquire(admission);
            if (launchLease is null)
            {
                return false;
            }

            try
            {
                _reservation = new CompositeReservation(launchLease, _reservation!);
                return true;
            }
            catch
            {
                launchLease.Dispose();
                throw;
            }
        }

        public CapacityDecision TransferToDecision(bool ollamaWarning)
        {
            var decision = new CapacityDecision(CapacityVerdict.Allow,
                ReasonAllow,
                ollamaWarning,
                _reservation);
            _reservation = null;
            return decision;
        }

        public void Dispose()
        {
            _reservation?.Dispose();
            _reservation = null;
        }
    }

    private sealed class CompositeReservation(IDisposable launchLease, IDisposable footprintReservation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                launchLease.Dispose();
            }
            finally
            {
                footprintReservation.Dispose();
            }
        }
    }
}
