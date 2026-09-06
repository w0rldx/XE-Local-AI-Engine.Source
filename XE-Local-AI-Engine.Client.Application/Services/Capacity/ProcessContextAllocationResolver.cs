namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Collections.Concurrent;
using System.Globalization;
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
public sealed class ProcessContextAllocationResolver(
    IGgufModelStore modelStore,
    IRuntimeDeviceAudit runtimeAudit,
    IProcessVramBudgetProbe processVramBudgetProbe,
    MemoryFitEstimator estimator,
    LlamaServerLaunchPolicyOptions options) : IProcessContextAllocationResolver
{
    private const int MaximumAutomaticDownTiers = 2;

    /// <summary>
    ///     The largest window the chat fallback will select when no tier fits with the model's weights resident. Kept in
    ///     step with the application's default conversation window (<c>ConversationContextBudgetOptions.DefaultContextTokens</c>,
    ///     8192) so the conversation budgeter's reserved output floor and always-keep turns still leave usable room for a
    ///     system prompt, tool definitions, and history.
    /// </summary>
    private const int FallbackContextCeilingTokens = 8192;

    private readonly ConcurrentDictionary<string, HardwareAllocationContext> _hardwareAllocationContexts =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessContextAllocation?>>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProcessContextAllocation> _adjustedAllocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _oomDownTiers = new(StringComparer.Ordinal);
    private readonly MemoryFitEstimator _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    private readonly IGgufModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
    private readonly LlamaServerLaunchPolicyOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly IProcessVramBudgetProbe _processVramBudgetProbe =
        processVramBudgetProbe ?? throw new ArgumentNullException(nameof(processVramBudgetProbe));

    private readonly IRuntimeDeviceAudit _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));

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

        var facts = await _modelStore.ResolveModelFootprintFactsAsync(modelName, ct).ConfigureAwait(false);
        if (facts is null || facts.ParamCount is not > 0 && facts.FileSizeBytes <= 0)
        {
            return null;
        }

        // Normalized to null for fp16 (and for anything unrecognized) BEFORE the cache key, so a caller that names the
        // default explicitly lands on the same cached allocation as one that names nothing at all.
        var kvCacheQuant = NormalizeKvCacheQuant(kvCacheType);
        var contentIdentity = facts.ContentIdentity ?? $"{modelName}:{facts.FileSizeBytes}";
        var key = BuildCacheKey(contentIdentity, role, variant, resolved, kvCacheQuant);
        var state = (Resolver: this, Key: key, ContentIdentity: contentIdentity, Role: role, Variant: variant, Resolved: resolved, Facts: facts,
            KvCacheQuant: kvCacheQuant);
        var lazy = _cache.GetOrAdd(key,
            static (_, captured) => new Lazy<Task<ProcessContextAllocation?>>(() => captured.Resolver.ResolveCoreAsync(captured.Key,
                    captured.ContentIdentity,
                    captured.Role,
                    captured.Variant,
                    captured.Resolved,
                    captured.Facts,
                    captured.KvCacheQuant,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication),
            state);

        var resolution = lazy.Value;
        ProcessContextAllocation? allocation;
        try
        {
            allocation = await resolution.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Caller cancellation only stops this waiter; the shared computation continues and remains cacheable.
            // A faulted/cancelled shared computation, however, must not poison this key for the process lifetime.
            if (resolution.IsFaulted || resolution.IsCanceled)
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<ProcessContextAllocation?>>>>)_cache)
                    .Remove(new KeyValuePair<string, Lazy<Task<ProcessContextAllocation?>>>(key, lazy));
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
        if (current.Source != ProcessContextAllocationSource.HardwareTier
            || !_hardwareAllocationContexts.TryGetValue(current.CacheKey, out var context)
            || context.Role != ModelRole.Chat
            || !TryGetNextTier(current.ProcessContextTokens, out var next))
        {
            return false;
        }

        downTiered = BuildAllocation(current.CacheKey,
            context.ContentIdentity,
            next,
            context.TrainCeiling,
            ProcessContextAllocationSource.HardwareTier,
            context.Variant,
            context.Profile,
            context.Facts,
            context.ProcessGpuBudget,
            context.KvCacheQuant);
        return true;
    }

    public bool TryCommitAdmissionAllocation(ProcessContextAllocation candidate, out ProcessContextAllocation committed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        committed = candidate;
        if (candidate.Source != ProcessContextAllocationSource.HardwareTier)
        {
            return true;
        }

        if (!_hardwareAllocationContexts.TryGetValue(candidate.CacheKey, out var context)
            || !string.Equals(candidate.ContentIdentity, context.ContentIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        committed = CommitAdjustedAllocation(candidate);
        return true;
    }

    public bool TryGetEffectiveCommittedAllocation(ProcessContextAllocation admitted, out ProcessContextAllocation effective)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        effective = admitted;
        if (admitted.Source != ProcessContextAllocationSource.HardwareTier)
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

            if (adjusted.ProcessContextTokens <= admitted.ProcessContextTokens)
            {
                effective = adjusted;
            }
        }

        return true;
    }

    public bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered)
    {
        ArgumentNullException.ThrowIfNull(current);
        downTiered = current;
        if (current.Source != ProcessContextAllocationSource.HardwareTier)
        {
            return false;
        }

        if (!_hardwareAllocationContexts.TryGetValue(current.CacheKey, out var context))
        {
            return false;
        }

        lock (context)
        {
            var effective = _adjustedAllocations.TryGetValue(current.CacheKey, out var adjusted)
                            && adjusted.ProcessContextTokens < current.ProcessContextTokens
                ? adjusted
                : current;
            if (!TryGetNextTier(effective.ProcessContextTokens, out var next))
            {
                return false;
            }

            var count = _oomDownTiers.AddOrUpdate(current.CacheKey, 1, static (_, prior) => prior + 1);
            if (count > MaximumAutomaticDownTiers)
            {
                return false;
            }

            var candidate = BuildAllocation(current.CacheKey,
                context.ContentIdentity,
                next,
                context.TrainCeiling,
                ProcessContextAllocationSource.HardwareTier,
                context.Variant,
                context.Profile,
                context.Facts,
                context.ProcessGpuBudget,
                context.KvCacheQuant);
            downTiered = CommitAdjustedAllocation(candidate);
            return true;
        }
    }

    private ProcessContextAllocation CommitAdjustedAllocation(ProcessContextAllocation candidate)
    {
        return _adjustedAllocations.AddOrUpdate(candidate.CacheKey,
            static (_, proposed) => proposed,
            static (_, existing, proposed) => existing.ProcessContextTokens <= proposed.ProcessContextTokens
                ? existing
                : proposed,
            candidate);
    }

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

    private async Task<ProcessContextAllocation?> ResolveCoreAsync(string key,
        string contentIdentity,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        GgufModelFootprintFacts facts,
        KvCacheQuant? kvCacheQuant,
        CancellationToken ct)
    {
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct).ConfigureAwait(false);
        var source = ProcessContextAllocationSource.HardwareTier;
        if (!resolved.ExploreMode && resolved.CtxSize > 0)
        {
            source = ProcessContextAllocationSource.FrozenProfile;
        }
        else if (ResolveDeterministicOverride(resolved) is > 0)
        {
            source = ProcessContextAllocationSource.DeterministicOverride;
        }

        var trainCeiling = ResolveTrainCeiling(facts.ContextLength);
        if (source == ProcessContextAllocationSource.FrozenProfile)
        {
            var frozenTokens = Math.Max(1, resolved.CtxSize);
            return BuildAllocation(key, contentIdentity, frozenTokens, trainCeiling, source, variant, profile, facts,
                await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false), kvCacheQuant);
        }

        if (source == ProcessContextAllocationSource.DeterministicOverride)
        {
            var overridden = CapAndAlign(ResolveDeterministicOverride(resolved)!.Value, trainCeiling);
            return BuildAllocation(key, contentIdentity, overridden, trainCeiling, source, variant, profile, facts,
                await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false), kvCacheQuant);
        }

        var processGpuBudget = await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false);
        _hardwareAllocationContexts[key] = new HardwareAllocationContext(contentIdentity,
            role,
            variant,
            profile,
            facts,
            processGpuBudget,
            trainCeiling,
            kvCacheQuant);
        var candidates = role == ModelRole.Chat
            ? LlamaServerLaunchPolicyOptions.ChatContextTiers
            : [_options.ContextTokensForRole(role)];

        foreach (var candidate in candidates)
        {
            // The tier is chosen against the fp16 estimate even when the caller named a quantized KV type. That keeps
            // the guarantee the whole option rests on — a quantized request can only ever reserve LESS — because a
            // quantized tier walk could otherwise SELECT a larger window and end up booking more bytes than fp16 would.
            var context = CapAndAlign(candidate, trainCeiling);
            var allocation = BuildAllocation(key, contentIdentity, context, trainCeiling, source, variant, profile, facts,
                processGpuBudget, kvCacheQuant: null);
            if (FitsStableBudgets(allocation.Footprint, variant, profile, processGpuBudget))
            {
                return kvCacheQuant is null
                    ? allocation
                    : BuildAllocation(key, contentIdentity, context, trainCeiling, source, variant, profile, facts,
                        processGpuBudget, kvCacheQuant);
            }
        }

        var fallback = ResolveFallbackContextTokens(role, candidates, facts, trainCeiling, variant, profile, processGpuBudget);
        return BuildAllocation(key, contentIdentity, fallback, trainCeiling, source, variant, profile, facts, processGpuBudget, kvCacheQuant);
    }

    /// <summary>
    ///     The window to launch with when no tier fits the stable budgets with the model's weights resident.
    ///     <para>
    ///         That situation does not mean the model cannot run: llama.cpp splits layers across the GPU and system RAM
    ///         (and memory-maps the rest from disk) rather than refusing to launch, and the weights term that overflowed
    ///         does not shrink with the context window — so collapsing to the smallest tier buys nothing and costs
    ///         everything. At 2048 tokens the conversation budgeter's reserved output floor claims half the window, the
    ///         agent scaffold alone overflows the always-keep set, and every send fails with a context-budget error that
    ///         reads as an application bug rather than an oversized model.
    ///     </para>
    ///     <para>
    ///         So the fallback walks down from the default conversation window instead, keeping the largest tier whose
    ///         window-scaled cost — the KV cache plus its share of the safety margin, i.e. everything the estimate adds
    ///         over a zero-context estimate — still fits the combined GPU + RAM budget the split placement draws on. It
    ///         never selects ABOVE that ceiling (no tier fit, so this is a degraded launch, not an opportunity), and it
    ///         still bottoms out at the smallest tier on a host that cannot hold even the KV cache. Auxiliary roles carry
    ///         a single configured window rather than a ladder, so they are returned unchanged.
    ///     </para>
    /// </summary>
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
        int contextTokens,
        int? trainCeiling,
        ProcessContextAllocationSource source,
        GpuVariant variant,
        HardwareProfile profile,
        GgufModelFootprintFacts facts,
        long? processGpuBudget,
        KvCacheQuant? kvCacheQuant)
    {
        var estimation = BuildEstimationContext(variant, profile, processGpuBudget);
        var useGpu = estimation.UseGpu;
        var gpuBudget = estimation.GpuBudget;

        var estimate = Estimate(facts, contextTokens, estimation.Profile, kvCacheQuant);
        ResourceFootprint footprint;
        ProcessPlacementMode placement;
        if (!useGpu)
        {
            footprint = new ResourceFootprint(0, estimate.EstimatedBytes);
            placement = ProcessPlacementMode.Cpu;
        }
        else if (estimate.MoeVerdict == MoeFitVerdict.FitsWithExpertOffload)
        {
            footprint = new ResourceFootprint(estimate.GpuBytes ?? gpuBudget,
                Math.Max(estimate.CpuBytes ?? 0, facts.FileSizeBytes));
            placement = ProcessPlacementMode.ExpertOffload;
        }
        else if (estimate.EstimatedBytes <= gpuBudget)
        {
            // llama.cpp memory-maps the GGUF; a fully GPU-resident placement does not commit a second file-sized RAM
            // allocation. Reserving the on-disk size here made admission reject VRAM-fitting models on low-free-RAM hosts.
            footprint = new ResourceFootprint(estimate.EstimatedBytes, RamBytes: 0);
            placement = ProcessPlacementMode.GpuResident;
        }
        else
        {
            var gpuBytes = Math.Max(0, gpuBudget);
            var ramBytes = checked(Math.Max(facts.FileSizeBytes, estimate.EstimatedBytes - gpuBytes));
            footprint = new ResourceFootprint(gpuBytes, ramBytes);
            placement = ProcessPlacementMode.Hybrid;
        }

        return new ProcessContextAllocation(contextTokens, trainCeiling, source, placement, footprint, contentIdentity, key);
    }

    /// <summary>
    ///     The reserve-adjusted budgets this resolver scores against, plus the synthetic profile that pins the estimator
    ///     to exactly those budgets. The process GPU budget has already been probed per backend and had the reserve taken
    ///     off it, so the raw free-VRAM reading is cleared from the profile: the estimator prefers a free-VRAM figure
    ///     when one is present, and leaving it here would silently score against a global reading this resolver has
    ///     deliberately narrowed to a per-process one.
    /// </summary>
    private static EstimationContext BuildEstimationContext(GpuVariant variant, HardwareProfile profile, long? processGpuBudget)
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
            moeFacts: new MoeFacts(ActiveParamCount: null, facts.ExpertCount, facts.ExpertUsedCount),
            attention: new GgufAttentionShape(facts.AttentionKeyLength,
                facts.AttentionValueLength,
                facts.SlidingWindow,
                facts.SlidingWindowPattern,
                facts.AttentionKeyLengthMla,
                facts.AttentionValueLengthMla),
            nativeQuantFormat: QuantLadder.IsNativeFormat(quant));
    }

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
        var probed = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(backend, ct)
                                                  .ConfigureAwait(false);
        return probed is > 0 ? probed : profile.VramBytes;
    }

    private static bool FitsStableBudgets(ResourceFootprint footprint, GpuVariant variant, HardwareProfile profile, long? processGpuBudget)
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

    private static long UsableGpuBudget(long total) =>
        Math.Max(0, total - Math.Max(LlamaServerLaunchPolicyOptions.MinimumGpuReserveBytes,
            (long)(total * LlamaServerLaunchPolicyOptions.GpuReserveFraction)));

    private static long UsableRamBudget(long total) =>
        Math.Max(0, total - Math.Max(LlamaServerLaunchPolicyOptions.MinimumRamReserveBytes,
            (long)(total * LlamaServerLaunchPolicyOptions.RamReserveFraction)));

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
    ///     The deterministic context-window override in force for this resolution: the request-scoped explore override
    ///     when one was supplied, otherwise the never-bound
    ///     <see cref="LlamaServerLaunchPolicyOptions.DeterministicContextTokensOverride" />. The request wins, so an
    ///     operator benchmark explore pins its own window without touching options that outlive the call.
    /// </summary>
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

    private sealed record HardwareAllocationContext(
        string ContentIdentity,
        ModelRole Role,
        GpuVariant Variant,
        HardwareProfile Profile,
        GgufModelFootprintFacts Facts,
        long? ProcessGpuBudget,
        int? TrainCeiling,
        KvCacheQuant? KvCacheQuant);
}
