namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat;

/// <summary>
///     Default <see cref="ICapacityService" />: cloud bypass, local-same-as-running queue, and the byte-budget plus
///     process-count decision, committed TOCTOU-safe under the pending-footprint ledger's decision gate.
/// </summary>
/// <remarks>See <c>docs/wiki/04-agent-mode.md</c> ("Capacity gate &amp; sub-agent spawn") for the four admission passes.</remarks>
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
        return await DecideAsync(new CapacityRequest
        {
            ModelName = modelName,
            Role = role
        }, ct);
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

        // Cloud short-circuit: a cloud-routed model costs this node no bytes and no process, so it is admitted unprobed. Keyed on modelName, not the
        // node-default selection, because RuntimeChatClient re-selects per send: an active Codex session must not exempt a spawn naming a local model.
        if (_cloudFactory.IsCloudProviderSelected(modelName))
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.Allow,
                Reason = ReasonAllowCloud,
                OllamaEvictionWarning = false
            };
        }

        var providerName = await _localProviderResolver.ResolveProviderNameForModelAsync(modelName, ct);
        var isOllama = string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase);
        var isLlamaServer = string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
        if (isLlamaServer && _externalEndpoints.Resolve(modelName, role) is not null)
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.Allow,
                Reason = ReasonAllowExternal,
                OllamaEvictionWarning = false
            };
        }

        // An operator-registered external model runs on someone else's hardware: no process, no weights, no RAM or VRAM here, and the byte-budget path
        // below would reject it as "footprint could not be determined". Both conditions are deliberate and neither grants trust: see docs/wiki/04-agent-mode.md ("Capacity gate & sub-agent spawn").
        if (string.Equals(providerName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
            || ExternalModelId.HasExternalScheme(modelName))
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.Allow,
                Reason = ReasonAllowExternal,
                OllamaEvictionWarning = false
            };
        }

        // Warm the device audit OUTSIDE the ledger gate: its bounded, cached --list-devices probe would otherwise serialize every capacity decision behind a
        // one-time probe. The gated read below reuses that cache, and the GPU-load admission gate is taken later, inside the spawn, never under this gate.
        await _runtimeAudit.GetAuditAsync(forceRefresh: false, ct);

        // The decide-commit gate serializes the read-decide-reserve so two concurrent different-model spawns cannot both
        // pass on the same snapshot. Held only for this short sequence — no inference runs under it.
        using var gate = await _ledger.EnterDecisionAsync(ct);

        var runningSnapshot = await SnapshotRunningKeysAsync(isOllama, ct);
        var running = runningSnapshot.Keys;

        // Already running for this (model, role): serialize on that process; no fit math, no second load.
        if (running.Contains(new RunningKey(modelName, role)))
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.QueueSameModel,
                Reason = ReasonQueueSameModel,
                OllamaEvictionWarning = false
            };
        }

        var ollamaWarning = isOllama && running.Count > 0;
        var launchSnapshot = isLlamaServer
            ? _launchAdmissions.Snapshot(modelName, role)
            : new ProcessLaunchAdmissionSnapshot
            {
                AdmittedKeys = new HashSet<ProcessLaunchAdmissionKey>(),
                HasRequestedKey = false,
                HasGlobalBlocker = false
            };
        if (launchSnapshot.HasRequestedKey || launchSnapshot.HasGlobalBlocker)
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.RejectInsufficient,
                Reason = ReasonRejectByteBudget,
                OllamaEvictionWarning = ollamaWarning
            };
        }

        // INVARIANT: the forced refresh runs UNDER the gate, never before it. The free-VRAM baseline nets out every resident model, so two racing decisions
        // sharing a pre-load snapshot would over-admit; the probe is wall-clock bounded, so holding the gate across it can never wedge the admission path.
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: true, ct);
        var footprint = await _footprintProvider
            .ResolveFootprintAsync(modelName, role, profile, request.RequiredContextTokens, request.KvCacheType, ct);
        if (!footprint.IsKnown)
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.RejectInsufficient,
                Reason = ReasonRejectFootprintUnknown,
                OllamaEvictionWarning = ollamaWarning
            };
        }

        // Process-count headroom mirrors the supervisor's loaded-cap (distinct (model,role) + this new one ≤ cap).
        var activeProcessKeys = running.Select(static key => new ProcessLaunchAdmissionKey(key.ModelName, key.Role))
                                       .Concat(launchSnapshot.AdmittedKeys)
                                       .ToHashSet();
        if (activeProcessKeys.Count + 1 > _localProviderResolver.MaxLoadedProcesses)
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.RejectInsufficient,
                Reason = ReasonRejectProcessCap,
                OllamaEvictionWarning = ollamaWarning
            };
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
                return new CapacityDecision
                {
                    Verdict = CapacityVerdict.RejectInsufficient,
                    Reason = ReasonRejectByteBudget,
                    OllamaEvictionWarning = ollamaWarning
                };
            }

            // A caller that NAMED a required window launches AT it (a benchmark replays its frozen -c), so a lower tier must never be admitted: the reservation
            // would under-book, and it pins the model's shared allocation below that window for the process lifetime — later admissions reject until restart.
            if (request.RequiredContextTokens is { } required
                && downTiered.Admission?.Allocation.ProcessContextTokens < required)
            {
                return new CapacityDecision
                {
                    Verdict = CapacityVerdict.RejectInsufficient,
                    Reason = ReasonRejectByteBudget,
                    OllamaEvictionWarning = ollamaWarning
                };
            }

            footprint = downTiered;
        }

        if (!_footprintProvider.TryCommitAdmissionFootprint(footprint, out footprint)
            || !FitsResourceBudget(profile, footprint.Resources, hasUnmeasuredGpuLoad))
        {
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.RejectInsufficient,
                Reason = ReasonRejectByteBudget,
                OllamaEvictionWarning = ollamaWarning
            };
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
            return new CapacityDecision
            {
                Verdict = CapacityVerdict.RejectInsufficient,
                Reason = ReasonRejectByteBudget,
                OllamaEvictionWarning = ollamaWarning
            };
        }

        return reservation.TransferToDecision(ollamaWarning);
    }

    /// <summary>Whether the footprint fits every non-zero resource axis of the live free baseline, less the in-flight ledger reservations.</summary>
    /// <remarks>
    ///     The free baseline already nets out resident loaded models, so only the ledger's reservations are subtracted. A zero axis needs no
    ///     measurement: a fully GPU-resident llama.cpp allocation memory-maps the GGUF and therefore carries no committed-RAM reservation.
    /// </remarks>
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

            // NVIDIA has an authoritative global-free reader, so a missing measurement fails closed: total VRAM cannot reveal residents outside the ledger and would over-admit.
            // Other vendors expose only total VRAM, which hides a resident process or a second draft GGUF: safe only for a first, non-external-draft launch, and the process cap is not accounting.
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

    /// <summary>The running <c>(model, role)</c> keys for the relevant local provider, or an explicit unknown state when the probe fails.</summary>
    /// <remarks>
    ///     llama.cpp reads the supervisor's per-process health rows; Ollama reads its running-models snapshot, where role is not modelled, so every
    ///     running model counts as a Chat process — the only role a sub-agent chat spawn competes with. A probe failure preserves the uncertainty:
    ///     CPU and free-VRAM decisions still have authoritative byte baselines, while the non-NVIDIA total-VRAM fallback must reject, because it
    ///     cannot establish whether unledgered residents already consume that total.
    /// </remarks>
    private async Task<RunningSnapshot> SnapshotRunningKeysAsync(bool isOllama, CancellationToken ct)
    {
        try
        {
            if (isOllama)
            {
                var snapshot = await _ollamaModelService.ListRunningModelsAsync(ct);
                return new RunningSnapshot(snapshot
                                           .Select(model => new RunningKey(model.ModelName ?? model.Name ?? string.Empty, ModelRole.Chat))
                                           .ToHashSet(),
                    IsKnown: true);
            }

            var health = await _supervisor.CheckHealthAsync(ct);

            // EXITED entries are not running: the supervisor keeps a corpse until the idle reaper collects it (up to a quarter of the idle TTL), and counting one burns
            // a slot and serializes the caller on a process that can never grant a lease. An unresponsive process still holds VRAM and a slot, so only HasExited is filtered.
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

    private sealed class AdmissionReservation : IDisposable
    {
        private IDisposable? _reservation;

        public AdmissionReservation(IDisposable footprintReservation)
        {
            ArgumentNullException.ThrowIfNull(footprintReservation);
            _reservation = footprintReservation;
        }

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
            var decision = new CapacityDecision
            {
                Verdict = CapacityVerdict.Allow,
                Reason = ReasonAllow,
                OllamaEvictionWarning = ollamaWarning,
                Reservation = _reservation
            };
            _reservation = null;
            return decision;
        }

        public void Dispose()
        {
            _reservation?.Dispose();
            _reservation = null;
        }
    }

    private sealed class CompositeReservation : IDisposable
    {
        private readonly IDisposable _launchLease;
        private readonly IDisposable _footprintReservation;
        private int _disposed;

        public CompositeReservation(IDisposable launchLease, IDisposable footprintReservation)
        {
            _launchLease = launchLease;
            _footprintReservation = footprintReservation;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _launchLease.Dispose();
            }
            finally
            {
                _footprintReservation.Dispose();
            }
        }
    }
}
