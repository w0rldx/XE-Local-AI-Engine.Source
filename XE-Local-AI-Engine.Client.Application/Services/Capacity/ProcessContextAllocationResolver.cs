namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<string, HardwareAllocationContext> _hardwareAllocationContexts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessContextAllocation?>>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProcessContextAllocation> _oomAdjustedAllocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _oomDownTiers = new(StringComparer.Ordinal);
    private readonly MemoryFitEstimator _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    private readonly IGgufModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
    private readonly LlamaServerLaunchPolicyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe =
        processVramBudgetProbe ?? throw new ArgumentNullException(nameof(processVramBudgetProbe));
    private readonly IRuntimeDeviceAudit _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));

    public async Task<ProcessContextAllocation?> ResolveAsync(
        string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(resolved);

        var facts = await _modelStore.ResolveModelFootprintFactsAsync(modelName, ct).ConfigureAwait(false);
        if (facts is null || facts.ParamCount is not > 0 && facts.FileSizeBytes <= 0)
        {
            return null;
        }

        var contentIdentity = facts.ContentIdentity ?? $"{modelName}:{facts.FileSizeBytes}";
        var key = BuildCacheKey(contentIdentity, role, variant, resolved);
        var state = (Resolver: this, Key: key, ContentIdentity: contentIdentity, Role: role, Variant: variant, Resolved: resolved, Facts: facts);
        var lazy = _cache.GetOrAdd(key,
            static (_, captured) => new Lazy<Task<ProcessContextAllocation?>>(
                () => captured.Resolver.ResolveCoreAsync(captured.Key,
                    captured.ContentIdentity,
                    captured.Role,
                    captured.Variant,
                    captured.Resolved,
                    captured.Facts,
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

        return allocation is not null && _oomAdjustedAllocations.TryGetValue(key, out var adjusted)
            ? adjusted
            : allocation;
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

        var next = 0;
        foreach (var tier in LlamaServerLaunchPolicyOptions.ChatContextTiers)
        {
            if (tier < current.ProcessContextTokens)
            {
                next = tier;
                break;
            }
        }
        if (next <= 0)
        {
            return false;
        }

        var count = _oomDownTiers.AddOrUpdate(current.CacheKey, 1, static (_, prior) => prior + 1);
        if (count > MaximumAutomaticDownTiers)
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
            context.ProcessGpuBudget);
        _oomAdjustedAllocations[current.CacheKey] = downTiered;
        return true;
    }

    private async Task<ProcessContextAllocation?> ResolveCoreAsync(
        string key,
        string contentIdentity,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        GgufModelFootprintFacts facts,
        CancellationToken ct)
    {
        var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct).ConfigureAwait(false);
        var source = ProcessContextAllocationSource.HardwareTier;
        if (!resolved.ExploreMode && resolved.CtxSize > 0)
        {
            source = ProcessContextAllocationSource.FrozenProfile;
        }
        else if (_options.DeterministicContextTokensOverride is > 0)
        {
            source = ProcessContextAllocationSource.DeterministicOverride;
        }

        var trainCeiling = ResolveTrainCeiling(facts.ContextLength);
        if (source == ProcessContextAllocationSource.FrozenProfile)
        {
            var frozenTokens = Math.Max(1, resolved.CtxSize);
            return BuildAllocation(key, contentIdentity, frozenTokens, trainCeiling, source, variant, profile, facts,
                await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false));
        }

        if (source == ProcessContextAllocationSource.DeterministicOverride)
        {
            var overridden = CapAndAlign(_options.DeterministicContextTokensOverride!.Value, trainCeiling);
            return BuildAllocation(key, contentIdentity, overridden, trainCeiling, source, variant, profile, facts,
                await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false));
        }

        var processGpuBudget = await ResolveProcessGpuBudgetAsync(variant, profile, ct).ConfigureAwait(false);
        _hardwareAllocationContexts[key] = new HardwareAllocationContext(contentIdentity,
            variant,
            profile,
            facts,
            processGpuBudget,
            trainCeiling);
        var candidates = role == ModelRole.Chat
            ? LlamaServerLaunchPolicyOptions.ChatContextTiers
            : [_options.ContextTokensForRole(role)];

        foreach (var candidate in candidates)
        {
            var context = CapAndAlign(candidate, trainCeiling);
            var allocation = BuildAllocation(key, contentIdentity, context, trainCeiling, source, variant, profile, facts,
                processGpuBudget);
            if (FitsStableBudgets(allocation.Footprint, variant, profile, processGpuBudget))
            {
                return allocation;
            }
        }

        var minimum = CapAndAlign(candidates[^1], trainCeiling);
        return BuildAllocation(key, contentIdentity, minimum, trainCeiling, source, variant, profile, facts, processGpuBudget);
    }

    private ProcessContextAllocation BuildAllocation(
        string key,
        string contentIdentity,
        int contextTokens,
        int? trainCeiling,
        ProcessContextAllocationSource source,
        GpuVariant variant,
        HardwareProfile profile,
        GgufModelFootprintFacts facts,
        long? processGpuBudget)
    {
        var useGpu = variant != GpuVariant.Cpu
                     && profile is { GpuAccelAvailable: true, VramKnown: true }
                     && processGpuBudget is > 0;
        var gpuBudget = useGpu ? UsableGpuBudget(processGpuBudget!.Value) : 0;
        var ramBudget = UsableRamBudget(profile.TotalRamBytes);
        var estimationProfile = profile with
        {
            VramKnown = useGpu,
            GpuAccelAvailable = useGpu,
            VramBytes = useGpu ? gpuBudget : null,
            AvailableVramBytes = null,
            AvailableRamBytes = ramBudget
        };

        var estimate = Estimate(facts, contextTokens, estimationProfile);
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

    private MemoryFitEstimate Estimate(GgufModelFootprintFacts facts, int contextTokens, HardwareProfile profile)
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
            moeFacts: new MoeFacts(ActiveParamCount: null, facts.ExpertCount, facts.ExpertUsedCount),
            attention: new GgufAttentionShape(facts.AttentionKeyLength,
                facts.AttentionValueLength,
                facts.SlidingWindow,
                facts.SlidingWindowPattern),
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

    private string BuildCacheKey(string contentIdentity, ModelRole role, GpuVariant variant, ResolvedLaunchArguments resolved)
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
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record HardwareAllocationContext(
        string ContentIdentity,
        GpuVariant Variant,
        HardwareProfile Profile,
        GgufModelFootprintFacts Facts,
        long? ProcessGpuBudget,
        int? TrainCeiling);
}
