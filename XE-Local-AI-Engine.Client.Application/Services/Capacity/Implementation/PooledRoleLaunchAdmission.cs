namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerPooledLaunchAdmission" />: runs a cold embedder, reranker or chat launch through
///     <see cref="ICapacityService" />.
/// </summary>
/// <remarks>
///     The capacity service is Scoped and depends on the supervisor that calls this hook, so it is resolved per call from a
///     fresh scope rather than injected. Disposing that scope cannot touch the returned reservation: it holds only the
///     singleton ledger and launch-admission registry. The embedder is booked but never refused on budget (ingestion depends
///     on it); the reranker is gated like any other model, and a refusal leaves search on fusion order. A chat launch unloads
///     idle chat models before it accepts a smaller window, and is refused only when neither makes it fit.
/// </remarks>
public sealed class PooledRoleLaunchAdmission : ILlamaServerPooledLaunchAdmission
{
    private const long Mib = 1024 * 1024;

    /// <summary>How long a chat admission waits, after an eviction, for the global free-VRAM reading to show the released memory.</summary>
    internal static readonly TimeSpan FreedVramWaitCap = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FreedVramPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long, in total per admission, a chat admission waits for a resident that only a request in flight keeps loaded to go idle.</summary>
    internal static readonly TimeSpan BusyResidentWaitCap = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan BusyResidentPollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PooledRoleLaunchAdmission> _logger;

    public PooledRoleLaunchAdmission(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<PooledRoleLaunchAdmission> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (role is not (ModelRole.Embedding or ModelRole.Reranker))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Only pooled roles are admitted by this hook.");
        }

        CapacityDecision decision;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            decision = await capacity.DecideAsync(new CapacityRequest
            {
                ModelName = modelName,
                Role = role,
                NeverRejectOnBudget = role == ModelRole.Embedding
            }, ct);
        }

        return ToReservation(decision);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Decided against GLOBAL free VRAM; the window was sized against the per-process budget, and the two are never merged. Decide at
    ///     the full tier; on a memory shortfall unload one idle chat model, wait for the freed VRAM to show, and decide again. A model only a
    ///     request in flight keeps loaded is waited for, up to <see cref="BusyResidentWaitCap" />; with nothing left to unload, allow the
    ///     smaller windows. Eviction and both waits run between decisions, never under a gate but the caller's; a background load skips them.
    ///     No free-VRAM reading: no-op.
    /// </remarks>
    public async Task<IDisposable?> AdmitChatAsync(string modelName,
        bool mayUnloadIdleModels,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>> evictIdleModel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(evictIdleModel);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var audit = services.GetRequiredService<IRuntimeDeviceAudit>();
        var profile = await audit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
        if (profile is not { GpuAccelAvailable: true, VramKnown: true, AvailableVramBytes: not null })
        {
            return null;
        }

        var capacity = services.GetRequiredService<ICapacityService>();
        var request = new CapacityRequest
        {
            ModelName = modelName,
            Role = ModelRole.Chat,
            AllowAdmissionDownTier = false,
            SupervisorEnforcesProcessCap = true
        };
        var evicted = new List<string>();
        var decision = await capacity.DecideAsync(request, ct);
        if (decision.IsMemoryShortfall)
        {
            var protectedModels = mayUnloadIdleModels ? await ResolveProtectedModelsAsync(services, ct) : [];
            long? busyWaitStarted = null;
            while (mayUnloadIdleModels && decision.IsMemoryShortfall)
            {
                // The decision just force-refreshed the profile, so the cached reading is the baseline the freed memory must rise above.
                var baseline = (await audit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct)).AvailableVramBytes;
                var eviction = await evictIdleModel(protectedModels, ct);
                if (eviction.EvictedModel is { } victim)
                {
                    evicted.Add(victim);
                    await WaitForFreedVramAsync(audit, baseline, ct);
                    decision = await capacity.DecideAsync(request, ct);
                    continue;
                }

                // A resident only briefly busy (a post-turn background request) goes idle within seconds: refusing around it would refuse the
                // ordinary switch of model right after a turn. A resident busy past the bound falls through to the smaller window and the refusal.
                if (eviction.BusyModel is not { } busy
                    || busyWaitStarted is { } started && _timeProvider.GetElapsedTime(started) >= BusyResidentWaitCap)
                {
                    break;
                }

                if (busyWaitStarted is null)
                {
                    busyWaitStarted = _timeProvider.GetTimestamp();
                    _logger.LogInformation("Chat admission for model {ModelName} waits up to {WaitSeconds} s for model {BusyModel} to finish its request before unloading it.",
                        modelName, BusyResidentWaitCap.TotalSeconds, busy);
                }

                await Task.Delay(BusyResidentPollInterval, _timeProvider, ct);
            }

            if (decision.IsMemoryShortfall)
            {
                decision = await capacity.DecideAsync(request with
                {
                    AllowAdmissionDownTier = true
                }, ct);
            }
        }

        var allocation = decision.Admission?.Allocation;
        _logger.LogInformation(
            "Chat admission for model {ModelName} ({LoadKind} load): {Verdict}; global free {GlobalFreeMiB} MiB, footprint {FootprintGpuMiB} MiB GPU, window {ContextTokens}, placement {Placement}, evicted [{Evicted}].",
            modelName, mayUnloadIdleModels ? "user" : "background", decision.Verdict, decision.Admission?.GlobalFreeVramBytesAtAdmission / Mib, allocation?.Footprint.GpuBytes / Mib,
            allocation?.ProcessContextTokens, allocation?.Placement, string.Join(", ", evicted));

        return ToReservation(decision);
    }

    private static IDisposable? ToReservation(CapacityDecision decision) =>
        decision.Verdict switch
        {
            CapacityVerdict.Allow => decision.Reservation,
            CapacityVerdict.QueueSameModel => null,
            _ => throw new LlamaCapacityRefusedException(decision.Reason)
        };

    /// <summary>The chat models a load must never unload: the keep-warm model, while keep-warm is on. The embedder and reranker are pooled roles the eviction never takes.</summary>
    private static async Task<IReadOnlyCollection<string>> ResolveProtectedModelsAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<INodeRuntimeSettings>();
        return await settings.GetKeepModelWarmEnabledAsync(ct) && await settings.GetKeepModelWarmModelNameAsync(ct) is { Length: > 0 } keepWarm
            ? [keepWarm]
            : [];
    }

    /// <summary>Re-probes global free VRAM until it rises above <paramref name="baseline" /> or <see cref="FreedVramWaitCap" /> has passed.</summary>
    /// <remarks>A killed process releases its VRAM asynchronously, so the decision right after the kill can still see it held.</remarks>
    private async Task WaitForFreedVramAsync(IRuntimeDeviceAudit audit, long? baseline, CancellationToken ct)
    {
        var started = _timeProvider.GetTimestamp();
        while (true)
        {
            var free = (await audit.GetEffectiveProfileAsync(forceRefreshProfile: true, ct)).AvailableVramBytes;
            if (baseline is null || free > baseline || _timeProvider.GetElapsedTime(started) >= FreedVramWaitCap)
            {
                return;
            }

            await Task.Delay(FreedVramPollInterval, _timeProvider, ct);
        }
    }
}
