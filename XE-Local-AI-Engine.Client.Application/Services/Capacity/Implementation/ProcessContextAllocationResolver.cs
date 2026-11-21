namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
/// Resolves one immutable process allocation per content/role/backend/argument/policy identity.
/// Global free VRAM is deliberately absent from the cache key and tier decision.
/// </summary>
/// <remarks>
///     A chat admission may re-place an explore allocation against live memory (<see cref="TryPlaceWithinLiveBudget" />), never cached. Every
///     later substitution (commit, effective, out-of-memory retry) keeps the admitted placement and budget. Stages: wiki 03, section 2.8.
///     A CPU answer for a GPU key priced from a timed-out probe is transient: not cached, and no substitution acts on it.
/// </remarks>
public sealed class ProcessContextAllocationResolver : IProcessContextAllocationResolver
{
    private const int MaximumAutomaticDownTiers = 2;

    /// <summary>The largest window the chat fallback will select when no tier fits with the model's weights resident.</summary>
    /// <remarks>
    ///     Kept in step with the application's default conversation window (<c>ConversationContextBudgetOptions.DefaultContextTokens</c>,
    ///     8192), so the conversation budgeter's reserved output floor and always-keep turns still leave usable room for a system prompt,
    ///     tool definitions and history.
    /// </remarks>
    private const int FallbackContextCeilingTokens = 8192;

    /// <summary>The smallest chat tier the walk still prefers fully GPU-resident over an expert-offload placement at a larger tier.</summary>
    /// <remarks>
    ///     Expert offload (<c>--cpu-moe</c>) streams every expert weight from system RAM: on a 32 GB card the 35B-A3B ran about 5x slower at
    ///     65,536 offloaded than at 32,768 resident. Below this window the larger offloaded one stays the better trade.
    /// </remarks>
    private const int ResidentPreferenceFloorTokens = 16384;

    /// <summary>What a chat process's background slot costs beyond its headroom KV: the second sequence's recurrent state and buffers.</summary>
    /// <remarks>
    ///     The estimator models neither, so this is a measured allowance, not a derivation: the largest non-KV growth the second slot showed was about
    ///     234 MiB (Qwen3.5-9B, hybrid; 27B about 40 MiB). Accepted ceiling: a flat figure over-reserves for a small dense model; derive it from
    ///     the GGUF's recurrent-state dimensions if a borderline model needs the bytes back.
    /// </remarks>
    private const long BackgroundSlotStateAllowanceBytes = 256L * 1024 * 1024;

    /// <summary>The smallest <c>--fit-target</c> margin a live-share Hybrid launches with: llama.cpp's own default margin.</summary>
    private const long MinimumFitTargetBytes = 1024L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, HardwareAllocationContext> _hardwareAllocationContexts =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessContextAllocation?>>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProcessContextAllocation> _adjustedAllocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _oomDownTiers = new(StringComparer.Ordinal);
    private readonly MemoryFitEstimator _estimator;
    private readonly IGgufModelStore _modelStore;
    private readonly LlamaServerLaunchPolicyOptions _options;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;
    private readonly IRuntimeDeviceAudit _runtimeAudit;
    private readonly bool _liveShareHybridEnabled;

    public ProcessContextAllocationResolver(IGgufModelStore modelStore,
        IRuntimeDeviceAudit runtimeAudit,
        IProcessVramBudgetProbe processVramBudgetProbe,
        MemoryFitEstimator estimator,
        LlamaServerLaunchPolicyOptions options)
        : this(modelStore, runtimeAudit, processVramBudgetProbe, estimator, options, IsWddmHost())
    {
    }

    /// <summary>Test seam: <paramref name="liveShareHybridEnabled" /> stands in for the Windows/WSL host check that gates the after-ladder Hybrid.</summary>
    internal ProcessContextAllocationResolver(IGgufModelStore modelStore,
        IRuntimeDeviceAudit runtimeAudit,
        IProcessVramBudgetProbe processVramBudgetProbe,
        MemoryFitEstimator estimator,
        LlamaServerLaunchPolicyOptions options,
        bool liveShareHybridEnabled)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processVramBudgetProbe);
        ArgumentNullException.ThrowIfNull(runtimeAudit);
        _estimator = estimator;
        _modelStore = modelStore;
        _options = options;
        _processVramBudgetProbe = processVramBudgetProbe;
        _runtimeAudit = runtimeAudit;
        _liveShareHybridEnabled = liveShareHybridEnabled;
    }

    public Task<ProcessContextAllocation?> ResolveAsync(string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        CancellationToken ct)
    {
        return ResolveAsync(modelName, role, variant, resolved, kvCacheType: null, ct);
    }

    public async Task<ProcessContextAllocation?> ResolveAsync(string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        string? kvCacheType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(resolved);

        var facts = await _modelStore.ResolveModelFootprintFactsAsync(modelName, ct);
        if (facts is null || facts.ParamCount is not > 0 && facts.FileSizeBytes <= 0)
        {
            return null;
        }

        // Normalized to null for fp16 (and for anything unrecognized) BEFORE the cache key, so a caller that names the
        // default explicitly lands on the same cached allocation as one that names nothing at all.
        var kvCacheQuant = NormalizeKvCacheQuant(kvCacheType);
        var contentIdentity = facts.ContentIdentity ?? $"{modelName}:{facts.FileSizeBytes}";
        var key = BuildCacheKey(contentIdentity, role, variant, resolved, kvCacheQuant);
        var state = new
        {
            Resolver = this,
            Key = key,
            ContentIdentity = contentIdentity,
            Role = role,
            Variant = variant,
            Resolved = resolved,
            Facts = facts,
            KvCacheQuant = kvCacheQuant
        };
        var lazy = _cache.GetOrAdd(key,
            static (_, captured) =>
            {
                // The computation gets its own cache entry through the holder, so it can evict exactly that entry when its answer is transient.
                var holder = new StrongBox<Lazy<Task<ProcessContextAllocation?>>>();
                holder.Value = new Lazy<Task<ProcessContextAllocation?>>(() => captured.Resolver.ResolveCoreAsync(captured.Key,
                        holder,
                        captured.ContentIdentity,
                        captured.Role,
                        captured.Variant,
                        captured.Resolved,
                        captured.Facts,
                        captured.KvCacheQuant,
                        CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                return holder.Value;
            },
            state);

        var resolution = lazy.Value;
        ProcessContextAllocation? allocation;
        try
        {
            allocation = await resolution.WaitAsync(ct);
        }
        catch
        {
            // Caller cancellation only stops this waiter; the shared computation continues and decides its own cacheability.
            // A faulted/cancelled shared computation, however, must not poison this key for the process lifetime.
            if (resolution.IsFaulted || resolution.IsCanceled)
            {
                EvictCachedComputation(key, lazy);
            }

            throw;
        }

        return allocation is not null && _adjustedAllocations.TryGetValue(key, out var adjusted)
            ? adjusted
            : allocation;
    }

    public bool TryDownTierForAdmission(ProcessContextAllocation current, out ProcessContextAllocation downTiered)
    {
        ArgumentNullException.ThrowIfNull(current);
        downTiered = current;
        if (current.IsTransient || !_hardwareAllocationContexts.TryGetValue(current.CacheKey, out var context) || context.Role != ModelRole.Chat)
        {
            return false;
        }

        // The background slot goes first, at the same window: slot 2 never costs the window. A frozen or overridden window keeps its size.
        if (current.ChatSlots > 1)
        {
            downTiered = BuildAllocation(current.CacheKey, context, current.ProcessContextTokens, chatSlots: 1, current.Source);
            return true;
        }

        if (current.Source != ProcessContextAllocationSource.HardwareTier || !TryGetNextTier(current.ProcessContextTokens, out var next))
        {
            return false;
        }

        downTiered = BuildPreferringTwoSlots(current.CacheKey, context, next, ProcessContextAllocationSource.HardwareTier, context.KvCacheQuant);
        return true;
    }

    public bool TryCommitAdmissionAllocation(ProcessContextAllocation candidate, out ProcessContextAllocation committed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        committed = candidate;
        if (candidate.Source != ProcessContextAllocationSource.HardwareTier || candidate.IsTransient)
        {
            return true;
        }

        if (!_hardwareAllocationContexts.TryGetValue(candidate.CacheKey, out var context)
            || !string.Equals(candidate.ContentIdentity, context.ContentIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        // An admission down-tier is NOT cached: it reflects the residents of that moment, and the admission carries its window into the launch on its own, so
        // the next spawn after they leave tiers back up. Only an out-of-memory down-tier is sticky, and it keeps the candidate's placement.
        if (_adjustedAllocations.TryGetValue(candidate.CacheKey, out var adjusted) && IsSmaller(adjusted, candidate))
        {
            committed = BuildAllocationAtPlacement(context, candidate, adjusted.ProcessContextTokens, adjusted.ChatSlots);
        }

        return true;
    }

    public bool TryGetEffectiveCommittedAllocation(ProcessContextAllocation admitted, out ProcessContextAllocation effective)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        effective = admitted;
        if (admitted.Source != ProcessContextAllocationSource.HardwareTier || admitted.IsTransient)
        {
            return true;
        }

        if (!_hardwareAllocationContexts.TryGetValue(admitted.CacheKey, out var context)
            || !string.Equals(admitted.ContentIdentity, context.ContentIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        if (_adjustedAllocations.TryGetValue(admitted.CacheKey, out var adjusted))
        {
            if (!string.Equals(adjusted.CacheKey, admitted.CacheKey, StringComparison.Ordinal)
                || !string.Equals(adjusted.ContentIdentity, admitted.ContentIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            // Only a strictly smaller out-of-memory window replaces the admitted one, rebuilt at the admitted placement and budget.
            if (IsSmaller(adjusted, admitted))
            {
                effective = BuildAllocationAtPlacement(context, admitted, adjusted.ProcessContextTokens, adjusted.ChatSlots);
            }
        }

        return true;
    }

    public bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered)
    {
        ArgumentNullException.ThrowIfNull(current);
        downTiered = current;
        // A transient launch is not retried smaller: its cap would be priced on a context it never had, and the next launch re-resolves.
        if (current.IsTransient || !_hardwareAllocationContexts.TryGetValue(current.CacheKey, out var context))
        {
            return false;
        }

        lock (context)
        {
            var effective = _adjustedAllocations.TryGetValue(current.CacheKey, out var adjusted) && IsSmaller(adjusted, current)
                ? adjusted
                : current;

            // A chat process drops its background slot before its window, whatever chose the window, and never gets it back after an out-of-memory
            // start. The drop is no window step, so it leaves MaximumAutomaticDownTiers whole for the tier walk.
            if (context.Role == ModelRole.Chat && effective.ChatSlots > 1)
            {
                // The stable-priced cap is what sticks; the retry keeps the placement and budget the failed launch was booked at.
                var slotCap = CommitAdjustedAllocation(BuildAllocation(current.CacheKey, context, effective.ProcessContextTokens, chatSlots: 1, effective.Source));
                downTiered = BuildAllocationAtPlacement(context, current, slotCap.ProcessContextTokens, slotCap.ChatSlots);
                return true;
            }

            if (current.Source != ProcessContextAllocationSource.HardwareTier || !TryGetNextTier(effective.ProcessContextTokens, out var next))
            {
                return false;
            }

            var count = _oomDownTiers.AddOrUpdate(current.CacheKey, 1, static (_, prior) => prior + 1);
            if (count > MaximumAutomaticDownTiers)
            {
                return false;
            }

            var windowCap = CommitAdjustedAllocation(BuildAllocation(current.CacheKey, context, next, chatSlots: 1, ProcessContextAllocationSource.HardwareTier));
            downTiered = BuildAllocationAtPlacement(context, current, windowCap.ProcessContextTokens, windowCap.ChatSlots);
            return true;
        }
    }

    public bool TryPlaceWithinLiveBudget(ProcessContextAllocation current, LiveBudget live, PlacementStage stage, out ProcessContextAllocation placed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(live);
        placed = current;
        if (current.Source != ProcessContextAllocationSource.HardwareTier
            || current.IsTransient
            || !_hardwareAllocationContexts.TryGetValue(current.CacheKey, out var context)
            || context.Role != ModelRole.Chat
            || !string.Equals(current.ContentIdentity, context.ContentIdentity, StringComparison.Ordinal)
            || !BuildEstimationContext(context.Variant, context.Profile, context.ProcessGpuBudget).UseGpu)
        {
            return false;
        }

        var liveGpuBudget = live.GlobalFreeGpuBytes - live.ReservedGpuBytes;
        if (liveGpuBudget <= 0)
        {
            return false;
        }

        return stage == PlacementStage.BeforeLadder
            ? TryPlaceExpertOffload(current, context, liveGpuBudget, out placed)
            : TryPlaceLiveShareHybrid(current, context, live, liveGpuBudget, out placed);
    }

    /// <summary>The tier walk at the live GPU budget, kept only when it lands on expert offload; a resident answer is left to the ladder.</summary>
    private bool TryPlaceExpertOffload(ProcessContextAllocation current, HardwareAllocationContext context, long liveGpuBudget, out ProcessContextAllocation placed)
    {
        placed = current;
        if (context.Facts.ExpertCount is not > 0)
        {
            return false;
        }

        var walked = WalkTiers(current.CacheKey, context with
        {
            ProcessGpuBudget = liveGpuBudget
        }, ProcessContextAllocationSource.HardwareTier);
        if (walked.Placement != ProcessPlacementMode.ExpertOffload)
        {
            return false;
        }

        placed = walked;
        return true;
    }

    /// <summary>A single-slot allocation at the live GPU share: the largest chat tier at or below the current window and any OOM cap whose RAM fits live RAM.</summary>
    /// <remarks>Windows and WSL only, where the child sees a stable per-process budget rather than live free memory.</remarks>
    private bool TryPlaceLiveShareHybrid(ProcessContextAllocation current,
        HardwareAllocationContext context,
        LiveBudget live,
        long liveGpuBudget,
        out ProcessContextAllocation placed)
    {
        placed = current;
        if (!_liveShareHybridEnabled || UsableGpuBudget(liveGpuBudget) <= LlamaServerLaunchPolicyOptions.MinimumGpuReserveBytes)
        {
            return false;
        }

        var ceiling = _adjustedAllocations.TryGetValue(current.CacheKey, out var adjusted)
            ? Math.Min(current.ProcessContextTokens, adjusted.ProcessContextTokens)
            : current.ProcessContextTokens;
        var liveContext = context with
        {
            ProcessGpuBudget = liveGpuBudget
        };
        var freeRam = live.AvailableRamBytes - live.ReservedRamBytes;
        foreach (var tier in LlamaServerLaunchPolicyOptions.ChatContextTiers)
        {
            var window = CapAndAlign(tier, context.TrainCeiling);
            if (window > ceiling)
            {
                continue;
            }

            var candidate = BuildAllocation(current.CacheKey, liveContext, window, chatSlots: 1, ProcessContextAllocationSource.HardwareTier);
            if (candidate.Footprint.RamBytes > freeRam || candidate.Footprint.GpuBytes > liveGpuBudget)
            {
                continue;
            }

            placed = candidate.Placement == ProcessPlacementMode.Hybrid
                ? candidate with
                {
                    FitTargetBytes = Math.Max(MinimumFitTargetBytes,
                        Math.Max(context.ProcessGpuBudget ?? 0, live.GlobalFreeGpuBytes) - candidate.Footprint.GpuBytes)
                }
                : candidate;
            return true;
        }

        return false;
    }

    /// <summary>A smaller window priced at <paramref name="like" />'s placement budget and kept in its offload or Hybrid placement.</summary>
    /// <remarks>A substitution after admission never re-places at the stable budget, so it never grows on either axis.</remarks>
    private ProcessContextAllocation BuildAllocationAtPlacement(HardwareAllocationContext context, ProcessContextAllocation like, int contextTokens, int chatSlots)
    {
        var priced = context with
        {
            ProcessGpuBudget = like.GpuPlacementBudgetBytes ?? context.ProcessGpuBudget
        };
        var forced = like.Placement is ProcessPlacementMode.ExpertOffload or ProcessPlacementMode.Hybrid ? like.Placement : (ProcessPlacementMode?)null;
        var rebuilt = BuildAllocation(like.CacheKey,
            priced.ContentIdentity,
            priced.Role == ModelRole.Chat ? chatSlots : 1,
            contextTokens,
            priced.TrainCeiling,
            like.Source,
            priced.Variant,
            priced.Profile,
            priced.Facts,
            priced.ProcessGpuBudget,
            priced.KvCacheQuant,
            forced);
        return rebuilt.Placement == ProcessPlacementMode.Hybrid
            ? rebuilt with
            {
                FitTargetBytes = like.FitTargetBytes
            }
            : rebuilt;
    }

    private static bool IsWddmHost()
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return WslHost.Detect();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unknown host: keep native-Linux behaviour, which never books a live share.
            return false;
        }
    }

    private ProcessContextAllocation CommitAdjustedAllocation(ProcessContextAllocation candidate)
    {
        return _adjustedAllocations.AddOrUpdate(candidate.CacheKey,
            static (_, proposed) => proposed,
            static (_, existing, proposed) => IsSmaller(proposed, existing) ? proposed : existing,
            candidate);
    }

    // Window first, then slot count: a smaller allocation launches a smaller window, or the same window with fewer chat slots.
    private static bool IsSmaller(ProcessContextAllocation left, ProcessContextAllocation right) =>
        left.ProcessContextTokens < right.ProcessContextTokens
        || (left.ProcessContextTokens == right.ProcessContextTokens && left.ChatSlots < right.ChatSlots);

    private static bool TryGetNextTier(int currentTokens, out int next)
    {
        var candidate = LlamaServerLaunchPolicyOptions.ChatContextTiers
                                                      .Where(tier => tier < currentTokens)
                                                      .Select(static tier => (int?)tier)
                                                      .FirstOrDefault();
        if (candidate is null)
        {
            next = 0;
            return false;
        }

        next = candidate.Value;
        return true;
    }

    /// <summary>
    ///     The estimator's KV element size for a llama.cpp cache-type token, or <see langword="null" /> for the fp16
    ///     default. Conservative on uncertainty by construction: an unrecognized token reserves fp16 bytes.
    /// </summary>
    private static KvCacheQuant? NormalizeKvCacheQuant(string? kvCacheType)
    {
        // The token vocabulary belongs to LlamaServerKvCacheTypes; an unrecognized one normalizes away and reserves
        // fp16 bytes, so a type added there without a mapping here is conservative rather than wrong.
        if (!LlamaServerKvCacheTypes.TryNormalize(kvCacheType, out var normalized) || normalized is null)
        {
            return null;
        }

        if (string.Equals(normalized, LlamaServerKvCacheTypes.Q8_0, StringComparison.Ordinal))
        {
            return KvCacheQuant.Q8_0;
        }

        return string.Equals(normalized, LlamaServerKvCacheTypes.Q4_0, StringComparison.Ordinal) ? KvCacheQuant.Q4_0 : null;
    }

    private void EvictCachedComputation(string key, Lazy<Task<ProcessContextAllocation?>> computation)
    {
        // Removes the entry only while it is still this computation, so a newer entry for the key survives.
        ((ICollection<KeyValuePair<string, Lazy<Task<ProcessContextAllocation?>>>>)_cache)
            .Remove(new KeyValuePair<string, Lazy<Task<ProcessContextAllocation?>>>(key, computation));
    }

    private async Task<ProcessContextAllocation?> ResolveCoreAsync(string key,
        StrongBox<Lazy<Task<ProcessContextAllocation?>>> computation,
        string contentIdentity,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        GgufModelFootprintFacts facts,
        KvCacheQuant? kvCacheQuant,
        CancellationToken ct)
    {
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
        var source = ProcessContextAllocationSource.HardwareTier;
        var deterministicOverride = ResolveDeterministicOverride(resolved);
        if (!resolved.ExploreMode && resolved.CtxSize > 0)
        {
            source = ProcessContextAllocationSource.FrozenProfile;
        }
        else if (deterministicOverride is > 0)
        {
            source = ProcessContextAllocationSource.DeterministicOverride;
        }

        var trainCeiling = ResolveTrainCeiling(facts.ContextLength);
        var processGpuBudget = await ResolveProcessGpuBudgetAsync(variant, profile, ct);

        // Recorded for every source: an overridden window keeps its size, but its chat process may still drop the background slot.
        var allocationContext = new HardwareAllocationContext
        {
            ContentIdentity = contentIdentity,
            Role = role,
            Variant = variant,
            Profile = profile,
            Facts = facts,
            ProcessGpuBudget = processGpuBudget,
            TrainCeiling = trainCeiling,
            KvCacheQuant = kvCacheQuant
        };
        var allocation = PriceAllocation(key, allocationContext, source, resolved, deterministicOverride);

        // A timed-out probe priced a GPU key on the CPU: hand that answer to this launch only, so the next resolve prices a real reading.
        if (profile.GpuProbeIndeterminate && variant != GpuVariant.Cpu && allocation.Placement == ProcessPlacementMode.Cpu)
        {
            EvictCachedComputation(key, computation.Value!);
            return allocation with
            {
                IsTransient = true
            };
        }

        _hardwareAllocationContexts[key] = allocationContext;
        return allocation;
    }

    private ProcessContextAllocation PriceAllocation(string key,
        HardwareAllocationContext allocationContext,
        ProcessContextAllocationSource source,
        ResolvedLaunchArguments resolved,
        int? deterministicOverride)
    {
        // One chat slot: the profile's -ngl fits a pool whose slot count it does not record. Accepted ceiling: background work shares the chat's
        // slot on a frozen profile; upgrade path: persist the fitted slot count on the profile (a schema change) and replay exactly that.
        if (source == ProcessContextAllocationSource.FrozenProfile)
        {
            return PriceFrozenReplay(key, allocationContext, resolved, allocationContext.KvCacheQuant);
        }

        if (source == ProcessContextAllocationSource.DeterministicOverride && deterministicOverride is { } overrideTokens)
        {
            return BuildPreferringTwoSlots(key, allocationContext, CapAndAlign(overrideTokens, allocationContext.TrainCeiling), source, allocationContext.KvCacheQuant);
        }

        return WalkTiers(key, allocationContext, source);
    }

    /// <summary>The role's tier walk at the context's GPU budget: the largest fitting window, resident preferred over offload down to the floor, else the fallback.</summary>
    private ProcessContextAllocation WalkTiers(string key, HardwareAllocationContext allocationContext, ProcessContextAllocationSource source)
    {
        var role = allocationContext.Role;
        var variant = allocationContext.Variant;
        var profile = allocationContext.Profile;
        var facts = allocationContext.Facts;
        var trainCeiling = allocationContext.TrainCeiling;
        var processGpuBudget = allocationContext.ProcessGpuBudget;
        var kvCacheQuant = allocationContext.KvCacheQuant;
        var candidates = role == ModelRole.Chat
            ? LlamaServerLaunchPolicyOptions.ChatContextTiers
            : [_options.ContextTokensForRole(role)];

        // The first fitting expert-offload tier at or above the floor, held while the walk looks for a resident one down to the floor.
        int? expertOffloadContext = null;
        foreach (var candidate in candidates)
        {
            // The tier is chosen against the fp16 estimate even when the caller named a quantized KV type, keeping the guarantee the whole option rests
            // on — a quantized request can only ever reserve LESS — because a quantized tier walk could SELECT a larger window and book more bytes.
            var context = CapAndAlign(candidate, trainCeiling);
            if (expertOffloadContext is not null && context < ResidentPreferenceFloorTokens)
            {
                break;
            }

            // The window is the outer loop: the background slot is kept only where it costs neither this window nor its residency.
            var allocation = BuildPreferringTwoSlots(key, allocationContext, context, source, kvCacheQuant: null);
            if (!FitsStableBudgets(allocation.Footprint, variant, profile, processGpuBudget))
            {
                continue;
            }

            if (allocation.Placement == ProcessPlacementMode.ExpertOffload && context >= ResidentPreferenceFloorTokens)
            {
                expertOffloadContext ??= context;
                continue;
            }

            if (expertOffloadContext is not null && allocation.Placement != ProcessPlacementMode.GpuResident)
            {
                continue;
            }

            return kvCacheQuant is null
                ? allocation
                : BuildAllocation(key, allocationContext, context, allocation.ChatSlots, source, kvCacheQuant);
        }

        if (expertOffloadContext is { } offloadContext)
        {
            return BuildPreferringTwoSlots(key, allocationContext, offloadContext, source, kvCacheQuant);
        }

        var fallback = ResolveFallbackContextTokens(role, candidates, facts, trainCeiling, variant, profile, processGpuBudget);
        return BuildPreferringTwoSlots(key, allocationContext, fallback, source, kvCacheQuant);
    }

    /// <summary>A frozen replay priced by its argv: an all-expert <c>-ot</c> at the expert-offload split, a partial placement never below today's booking.</summary>
    /// <remarks>
    ///     Today's booking is the stable-budget allocation at the replayed window. A partial <c>-ngl</c> (at or below the block count, zero, or any explicit
    ///     count when the block count is unknown) wins over the <c>-ot</c> and adds the full resident estimate to RAM; a partial expert <c>-ot</c> adds
    ///     the expert bytes, and over an expert-offload booking also takes the stable Hybrid GPU share. Table: wiki 07, "Frozen replay".
    /// </remarks>
    private ProcessContextAllocation PriceFrozenReplay(string key, HardwareAllocationContext context, ResolvedLaunchArguments resolved, KvCacheQuant? kvCacheQuant)
    {
        var window = Math.Max(1, resolved.CtxSize);
        var source = ProcessContextAllocationSource.FrozenProfile;
        var today = BuildAllocation(key, context, window, chatSlots: 1, source, kvCacheQuant);
        var estimation = BuildEstimationContext(context.Variant, context.Profile, context.ProcessGpuBudget);
        if (!estimation.UseGpu)
        {
            return today;
        }

        var facts = context.Facts;
        if (IsPartialGpuLayers(resolved.NGpuLayers, facts.BlockCount))
        {
            // Layers left on the CPU keep their weights and their KV in host RAM.
            return WithRamAtLeast(today, Estimate(facts, window, estimation.Profile, kvCacheQuant).EstimatedBytes);
        }

        var pin = LlamaServerManagedFlags.ClassifyExpertPin(resolved.OverrideTensor);
        if (pin == ExpertPinScope.None || facts.ExpertCount is not > 0)
        {
            return today;
        }

        if (pin == ExpertPinScope.AllExperts)
        {
            return BuildAllocation(key,
                context.ContentIdentity,
                chatSlots: 1,
                window,
                context.TrainCeiling,
                source,
                context.Variant,
                context.Profile,
                facts,
                context.ProcessGpuBudget,
                kvCacheQuant,
                ProcessPlacementMode.ExpertOffload);
        }

        var expertBytes = EstimateExpertOffload(facts, window, estimation.Profile, kvCacheQuant).CpuBytes ?? 0;

        // A replay emits no --cpu-moe, so experts of unpinned blocks load on the GPU, which an expert-offload booking leaves out: book the stable
        // Hybrid share instead. Accepted ceiling: conservative, not the exact layer layout; upgrade path: per-block expert pricing (deferred item C).
        if (today.Placement == ProcessPlacementMode.ExpertOffload)
        {
            today = BuildAllocation(key,
                context.ContentIdentity,
                chatSlots: 1,
                window,
                context.TrainCeiling,
                source,
                context.Variant,
                context.Profile,
                facts,
                context.ProcessGpuBudget,
                kvCacheQuant,
                ProcessPlacementMode.Hybrid);
        }

        return WithRamAtLeast(today, expertBytes);
    }

    // Full offload is block count + 1 (the output layer counts), so -ngl equal to the block count leaves block 0 on the CPU (llama.cpp i_gpu_start).
    // Negative counts are llama.cpp's all-layers and automatic sentinels.
    private static bool IsPartialGpuLayers(int? gpuLayers, long? blockCount) =>
        gpuLayers is >= 0 && (blockCount is not > 0 || gpuLayers <= blockCount);

    private static ProcessContextAllocation WithRamAtLeast(ProcessContextAllocation allocation, long ramBytes) =>
        allocation with
        {
            Footprint = allocation.Footprint with
            {
                RamBytes = Math.Max(allocation.Footprint.RamBytes, ramBytes)
            }
        };

    /// <summary>
    ///     The allocation at <paramref name="contextTokens" />: a chat process with its background slot when that keeps the single-slot
    ///     placement and fits the stable budgets, otherwise single-slot. Other roles always run one slot.
    /// </summary>
    private ProcessContextAllocation BuildPreferringTwoSlots(string key,
        HardwareAllocationContext context,
        int contextTokens,
        ProcessContextAllocationSource source,
        KvCacheQuant? kvCacheQuant)
    {
        var single = BuildAllocation(key, context, contextTokens, chatSlots: 1, source, kvCacheQuant);
        if (context.Role != ModelRole.Chat)
        {
            return single;
        }

        var pinned = BuildAllocation(key, context, contextTokens, LlamaServerChatSlots.Count, source, kvCacheQuant);
        return pinned.Placement == single.Placement && FitsStableBudgets(pinned.Footprint, context.Variant, context.Profile, context.ProcessGpuBudget)
            ? pinned
            : single;
    }

    private ProcessContextAllocation BuildAllocation(string key,
        HardwareAllocationContext context,
        int contextTokens,
        int chatSlots,
        ProcessContextAllocationSource source) =>
        BuildAllocation(key, context, contextTokens, chatSlots, source, context.KvCacheQuant);

    private ProcessContextAllocation BuildAllocation(string key,
        HardwareAllocationContext context,
        int contextTokens,
        int chatSlots,
        ProcessContextAllocationSource source,
        KvCacheQuant? kvCacheQuant) =>
        BuildAllocation(key,
            context.ContentIdentity,
            context.Role == ModelRole.Chat ? chatSlots : 1,
            contextTokens,
            context.TrainCeiling,
            source,
            context.Variant,
            context.Profile,
            context.Facts,
            context.ProcessGpuBudget,
            kvCacheQuant);

    /// <summary>The window to launch with when no tier fits the stable budgets with the model's weights resident.</summary>
    /// <remarks>
    ///     Collapsing to the smallest tier buys nothing — llama.cpp splits layers across GPU and system RAM rather than refusing to launch,
    ///     and the weights term that overflowed does not shrink with the window — so the walk starts at
    ///     <see cref="FallbackContextCeilingTokens" /> and never selects ABOVE it, this being a degraded launch rather than an opportunity.
    ///     The ladder, the window-scaled cost it scores and the 2048-token failure it avoids are in
    ///     <c>docs/wiki/03-local-runtime-and-providers.md</c> ("2.8 Process context allocation and the fallback window").
    /// </remarks>
    private int ResolveFallbackContextTokens(ModelRole role,
        IReadOnlyList<int> candidates,
        GgufModelFootprintFacts facts,
        int? trainCeiling,
        GpuVariant variant,
        HardwareProfile profile,
        long? processGpuBudget)
    {
        var smallest = CapAndAlign(candidates[^1], trainCeiling);
        if (role != ModelRole.Chat)
        {
            return smallest;
        }

        var estimation = BuildEstimationContext(variant, profile, processGpuBudget);
        var weightsOnlyBytes = Estimate(facts, contextTokens: 0, estimation.Profile, kvCacheQuant: null).EstimatedBytes;
        var combinedBudget = estimation.GpuBudget + estimation.RamBudget;

        foreach (var candidate in LlamaServerLaunchPolicyOptions.ChatContextTiers)
        {
            if (candidate > FallbackContextCeilingTokens)
            {
                continue;
            }

            var context = CapAndAlign(candidate, trainCeiling);
            var windowBytes = Estimate(facts, context, estimation.Profile, kvCacheQuant: null).EstimatedBytes - weightsOnlyBytes;
            if (windowBytes <= combinedBudget)
            {
                return context;
            }
        }

        return smallest;
    }

    private ProcessContextAllocation BuildAllocation(string key,
        string contentIdentity,
        int chatSlots,
        int contextTokens,
        int? trainCeiling,
        ProcessContextAllocationSource source,
        GpuVariant variant,
        HardwareProfile profile,
        GgufModelFootprintFacts facts,
        long? processGpuBudget,
        KvCacheQuant? kvCacheQuant,
        ProcessPlacementMode? forcedPlacement = null)
    {
        var estimation = BuildEstimationContext(variant, profile, processGpuBudget);
        var useGpu = estimation.UseGpu;
        var gpuBudget = estimation.GpuBudget;

        // A chat process with its background slot (LlamaServerChatSlots): the slot's headroom KV rides the window, its state the allowance.
        var backgroundSlot = chatSlots > 1;
        var estimatedTokens = backgroundSlot ? checked(contextTokens + LlamaServerChatSlots.BackgroundHeadroomTokens) : contextTokens;
        var estimate = Estimate(facts, estimatedTokens, estimation.Profile, kvCacheQuant);
        if (useGpu
            && forcedPlacement == ProcessPlacementMode.ExpertOffload
            && estimate.MoeVerdict != MoeFitVerdict.FitsWithExpertOffload
            && facts.ExpertCount is > 0)
        {
            estimate = EstimateExpertOffload(facts, estimatedTokens, estimation.Profile, kvCacheQuant);
        }

        var slotStateBytes = backgroundSlot ? BackgroundSlotStateAllowanceBytes : 0;
        var estimatedBytes = estimate.EstimatedBytes + slotStateBytes;
        ResourceFootprint footprint;
        ProcessPlacementMode placement;
        if (!useGpu)
        {
            footprint = new ResourceFootprint(0, estimatedBytes);
            placement = ProcessPlacementMode.Cpu;
        }
        else if (estimate.MoeVerdict == MoeFitVerdict.FitsWithExpertOffload && forcedPlacement != ProcessPlacementMode.Hybrid)
        {
            footprint = new ResourceFootprint((estimate.GpuBytes ?? gpuBudget) + slotStateBytes,
                Math.Max(estimate.CpuBytes ?? 0, facts.FileSizeBytes));
            placement = ProcessPlacementMode.ExpertOffload;
        }
        else if (estimatedBytes <= gpuBudget && forcedPlacement != ProcessPlacementMode.Hybrid)
        {
            // llama.cpp memory-maps the GGUF; a fully GPU-resident placement does not commit a second file-sized RAM
            // allocation. Reserving the on-disk size here made admission reject VRAM-fitting models on low-free-RAM hosts.
            footprint = new ResourceFootprint(estimatedBytes, RamBytes: 0);
            placement = ProcessPlacementMode.GpuResident;
        }
        else
        {
            var gpuBytes = Math.Max(0, gpuBudget);
            var ramBytes = checked(Math.Max(facts.FileSizeBytes, estimatedBytes - gpuBytes));
            footprint = new ResourceFootprint(gpuBytes, ramBytes);
            placement = ProcessPlacementMode.Hybrid;
        }

        return new ProcessContextAllocation
        {
            ProcessContextTokens = contextTokens,
            ModelTrainContextTokens = trainCeiling,
            Source = source,
            Placement = placement,
            Footprint = footprint,
            ContentIdentity = contentIdentity,
            CacheKey = key,
            ChatSlots = chatSlots,
            GpuPlacementBudgetBytes = useGpu ? processGpuBudget : null
        };
    }

    /// <summary>
    ///     The reserve-adjusted budgets this resolver scores against, plus the synthetic profile that pins the estimator to exactly those
    ///     budgets.
    /// </summary>
    /// <remarks>
    ///     The process GPU budget has already been probed per backend and had the reserve taken off it, so the raw free-VRAM reading is
    ///     cleared from the profile: the estimator prefers a free-VRAM figure when one is present, and leaving it here would silently score
    ///     against a global reading this resolver has deliberately narrowed to a per-process one.
    /// </remarks>
    private EstimationContext BuildEstimationContext(GpuVariant variant, HardwareProfile profile, long? processGpuBudget)
    {
        var useGpu = variant != GpuVariant.Cpu
                     && profile is { GpuAccelAvailable: true, VramKnown: true }
                     && processGpuBudget is > 0;
        var gpuBudget = useGpu ? UsableGpuBudget(processGpuBudget!.Value) : 0;
        var ramBudget = UsableRamBudget(profile.TotalRamBytes);
        return new EstimationContext(useGpu,
            gpuBudget,
            ramBudget,
            profile with
            {
                VramKnown = useGpu,
                GpuAccelAvailable = useGpu,
                VramBytes = useGpu ? gpuBudget : null,
                AvailableVramBytes = null,
                AvailableRamBytes = ramBudget
            });
    }

    private MemoryFitEstimate Estimate(GgufModelFootprintFacts facts, int contextTokens, HardwareProfile profile, KvCacheQuant? kvCacheQuant)
    {
        var quant = GgufQuantParser.StripDynamicPrefix(facts.Quant);
        return _estimator.Estimate(quant,
            facts.ParamCount,
            facts.FileSizeBytes,
            facts.BlockCount ?? 0,
            facts.AttentionHeadCountKV ?? 0,
            facts.EmbeddingLength ?? 0,
            facts.AttentionHeadCount ?? 0,
            contextTokens,
            profile,
            kvCacheQuantized: false,
            kvCacheQuant: kvCacheQuant,
            moeFacts: ToMoeFacts(facts),
            attention: ToAttentionShape(facts),
            nativeQuantFormat: QuantLadder.IsNativeFormat(quant));
    }

    private MemoryFitEstimate EstimateExpertOffload(GgufModelFootprintFacts facts, int contextTokens, HardwareProfile profile, KvCacheQuant? kvCacheQuant)
    {
        var quant = GgufQuantParser.StripDynamicPrefix(facts.Quant);
        return _estimator.EstimateExpertOffload(quant,
            facts.ParamCount,
            facts.FileSizeBytes,
            facts.BlockCount ?? 0,
            facts.AttentionHeadCountKV ?? 0,
            facts.EmbeddingLength ?? 0,
            facts.AttentionHeadCount ?? 0,
            contextTokens,
            profile,
            ToMoeFacts(facts),
            kvCacheQuant,
            ToAttentionShape(facts),
            QuantLadder.IsNativeFormat(quant));
    }

    private static MoeFacts ToMoeFacts(GgufModelFootprintFacts facts) =>
        new()
        {
            ActiveParamCount = null,
            ExpertCount = facts.ExpertCount,
            ExpertUsedCount = facts.ExpertUsedCount
        };

    private static GgufAttentionShape ToAttentionShape(GgufModelFootprintFacts facts) =>
        new()
        {
            KeyLength = facts.AttentionKeyLength,
            ValueLength = facts.AttentionValueLength,
            SlidingWindow = facts.SlidingWindow,
            SlidingWindowPattern = facts.SlidingWindowPattern,
            KeyLengthMla = facts.AttentionKeyLengthMla,
            ValueLengthMla = facts.AttentionValueLengthMla,
            FullAttentionInterval = facts.FullAttentionInterval
        };

    private async Task<long?> ResolveProcessGpuBudgetAsync(GpuVariant variant, HardwareProfile profile, CancellationToken ct)
    {
        if (variant == GpuVariant.Cpu || !profile.VramKnown || !profile.GpuAccelAvailable)
        {
            return null;
        }

        var backend = variant switch
        {
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => "cpu"
        };
        var probed = await _processVramBudgetProbe.GetProcessBudgetBytesOrNullAsync(backend, ct);
        return probed is > 0 ? probed : profile.VramBytes;
    }

    private bool FitsStableBudgets(ResourceFootprint footprint, GpuVariant variant, HardwareProfile profile, long? processGpuBudget)
    {
        var ramFits = footprint.RamBytes <= UsableRamBudget(profile.TotalRamBytes);
        if (variant == GpuVariant.Cpu || !profile.VramKnown || !profile.GpuAccelAvailable)
        {
            return footprint.GpuBytes == 0 && ramFits;
        }

        return processGpuBudget is > 0
               && footprint.GpuBytes <= UsableGpuBudget(processGpuBudget.Value)
               && ramFits;
    }

    private long UsableGpuBudget(long total) =>
        Math.Max(0, total - Math.Max(LlamaServerLaunchPolicyOptions.MinimumGpuReserveBytes,
            (long)(total * _options.GpuReserveFraction)));

    private long UsableRamBudget(long total) =>
        Math.Max(0, total - Math.Max(LlamaServerLaunchPolicyOptions.MinimumRamReserveBytes,
            (long)(total * _options.RamReserveFraction)));

    private int? ResolveTrainCeiling(long? contextLength)
    {
        if (contextLength is not > 0)
        {
            return null;
        }

        var ceiling = Math.Max(1, contextLength.Value - _options.ContextSafetyMarginTokens);
        return (int)Math.Min(ceiling, int.MaxValue);
    }

    /// <summary>
    ///     The deterministic context-window override in force for this resolution: the request-scoped explore override when one was supplied,
    ///     otherwise the never-bound <see cref="LlamaServerLaunchPolicyOptions.DeterministicContextTokensOverride" />.
    /// </summary>
    /// <remarks>The request wins, so an operator benchmark explore pins its own window without touching options that outlive the call.</remarks>
    private int? ResolveDeterministicOverride(ResolvedLaunchArguments resolved)
    {
        return resolved.ExploreContextTokensOverride ?? _options.DeterministicContextTokensOverride;
    }

    private static int CapAndAlign(int requested, int? trainCeiling)
    {
        var capped = trainCeiling is { } ceiling ? Math.Min(requested, ceiling) : requested;
        if (capped < LlamaServerLaunchPolicyOptions.ContextAlignmentTokens)
        {
            return Math.Max(1, capped);
        }

        return capped / LlamaServerLaunchPolicyOptions.ContextAlignmentTokens
               * LlamaServerLaunchPolicyOptions.ContextAlignmentTokens;
    }

    private string BuildCacheKey(string contentIdentity, ModelRole role, GpuVariant variant, ResolvedLaunchArguments resolved, KvCacheQuant? kvCacheQuant)
    {
        var canonical = string.Join('|',
            contentIdentity,
            role,
            variant,
            resolved.ExploreMode,
            resolved.CtxSize,
            resolved.NGpuLayers,
            resolved.TensorSplit,
            resolved.OverrideTensor,
            resolved.KvTypeK,
            resolved.KvTypeV,
            resolved.FlashAttn,
            LlamaServerLaunchPolicyOptions.ContextAllocationPolicyVersion,
            _options.DeterministicContextTokensOverride,
            _options.ContextTokensForRole(role),
            _options.ContextSafetyMarginTokens);

        // Appended only when a quantized KV term was asked for, so the default (fp16) key stays byte-identical to the
        // one every non-benchmark caller has always produced — same string, same cache entry, same allocation.
        if (kvCacheQuant is not null)
        {
            canonical = $"{canonical}|kv:{kvCacheQuant}";
        }

        // Same conditional shape, same reason: an explore that named no override reproduces the pre-override key byte
        // for byte, while two explores with different windows can never share one cached allocation.
        if (resolved.ExploreContextTokensOverride is { } exploreOverride)
        {
            canonical = $"{canonical}|ctxo:{exploreOverride.ToString(CultureInfo.InvariantCulture)}";
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private readonly record struct EstimationContext(bool UseGpu, long GpuBudget, long RamBudget, HardwareProfile Profile);

    private sealed record HardwareAllocationContext
    {
        public required string ContentIdentity { get; init; }

        public required ModelRole Role { get; init; }

        public required GpuVariant Variant { get; init; }

        public required HardwareProfile Profile { get; init; }

        public required GgufModelFootprintFacts Facts { get; init; }

        public required long? ProcessGpuBudget { get; init; }

        public required int? TrainCeiling { get; init; }

        public required KvCacheQuant? KvCacheQuant { get; init; }
    }
}
