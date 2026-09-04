namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Default <see cref="ILlamaServerLaunchPolicy" />: turns <see cref="LlamaServerLaunchPolicyOptions" /> plus the
///     recorded safe-fallback state into a concrete <see cref="LlamaServerLaunchPlan" /> for one spawn.
/// </summary>
internal sealed class LlamaServerLaunchPolicy : ILlamaServerLaunchPolicy
{
    private readonly ILlamaServerLaunchFallbackStore _fallbackStore;
    private readonly ILogger<LlamaServerLaunchPolicy> _logger;
    private readonly LlamaServerLaunchPolicyOptions _options;

    public LlamaServerLaunchPolicy(LlamaServerLaunchPolicyOptions options,
        ILlamaServerLaunchFallbackStore fallbackStore,
        ILogger<LlamaServerLaunchPolicy>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _fallbackStore = fallbackStore ?? throw new ArgumentNullException(nameof(fallbackStore));
        _logger = logger ?? NullLogger<LlamaServerLaunchPolicy>.Instance;
    }

    /// <inheritdoc />
    public async Task<LlamaServerLaunchPlan> ResolveAsync(ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        ProcessContextAllocation allocation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(allocation);

        var (cpuThreads, cpuThreadsBatch) = ResolveCpuThreads(variant);

        // A CPU build never replays a frozen GPU profile (its -ngl/-ts/-ot/-ctk are GPU-specific), so the CPU spawn
        // always gets the deterministic policy context plus the CPU thread policy — regardless of
        // whether a profile exists.
        if (variant == GpuVariant.Cpu)
        {
            return new LlamaServerLaunchPlan(allocation.ProcessContextTokens,
                UseKvCacheQuantization: false,
                _options.KvCacheType,
                cpuThreads,
                cpuThreadsBatch);
        }

        // GPU: a frozen-profile replay pins its own -c / KV / FA verbatim (highest precedence), so the policy leaves the
        // context and KV to the replay args (and a GPU spawn carries no CPU threads).
        if (!resolved.ExploreMode)
        {
            return new LlamaServerLaunchPlan(RequestedContextTokens: null,
                UseKvCacheQuantization: false,
                _options.KvCacheType,
                CpuThreads: null,
                CpuThreadsBatch: null);
        }

        // GPU explore: the shared allocation's context plus the KV-cache quantization + flash attention optimization,
        // unless this backend already had the optimized config recorded as unable to reach readiness.
        var useKvQuant = _options.EnableGpuKvCacheQuantization
                         && !await _fallbackStore.IsOptimizedConfigDisabledAsync(variant, _options.KvCacheType, ct).ConfigureAwait(false);

        // --cpu-moe is emitted from the ADMITTED placement, never from an architecture name: only
        // MoeFitVerdict.FitsWithExpertOffload produces ExpertOffload, and that needs a positive expert_count in the
        // GGUF header. The flag makes the placement the ledger already booked true — see LlamaServerLaunchPlan.CpuMoe.
        return new LlamaServerLaunchPlan(allocation.ProcessContextTokens,
            useKvQuant,
            _options.KvCacheType,
            CpuThreads: null,
            CpuThreadsBatch: null,
            allocation.Placement == ProcessPlacementMode.ExpertOffload);
    }

    /// <inheritdoc />
    public LlamaServerLaunchPlan ResolveCpuReplayPlan(ResolvedLaunchArguments resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var (cpuThreads, cpuThreadsBatch) = ResolveCpuThreads(GpuVariant.Cpu);

        // Explore mode pins no context of its own, so there is nothing to carry over; a replay's frozen -c is the only
        // context a policy-free CPU spawn can honour.
        var requestedContextTokens = resolved.ExploreMode || resolved.CtxSize <= 0
            ? (int?)null
            : resolved.CtxSize;

        return new LlamaServerLaunchPlan(requestedContextTokens,
            UseKvCacheQuantization: false,
            _options.KvCacheType,
            cpuThreads,
            cpuThreadsBatch);
    }

    /// <inheritdoc />
    public Task RecordOptimizedConfigFailedAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
    {
        _logger.LogWarning("Recording optimized llama-server launch config (KV-cache quant + flash attention) as unsupported for backend {Variant} at KV-cache type {KvCacheType}; future spawns of that pair will use the safe config.", variant, kvCacheType);
        return _fallbackStore.DisableOptimizedConfigAsync(variant, kvCacheType, ct);
    }

    /// <summary>Derives (<c>-t</c>, <c>-tb</c>) for a CPU build; a GPU build gets both null — no thread flags.</summary>
    private CpuThreadPlan ResolveCpuThreads(GpuVariant variant)
    {
        if (variant != GpuVariant.Cpu || !_options.EnableCpuThreadPolicy)
        {
            return new CpuThreadPlan(Threads: null, ThreadsBatch: null);
        }

        // Environment.ProcessorCount is the LOGICAL core count; estimate physical cores by halving it when SMT is
        // assumed (the common x86 desktop case). Only a heuristic — the explicit overrides win for atypical topologies.
        var logical = Environment.ProcessorCount;
        var physical = _options.AssumeSimultaneousMultithreading && logical >= 2 ? logical / 2 : logical;
        physical = Math.Max(physical, 1);

        // Generation threads: physical minus a small host reserve (floor 1) so inference does not starve the host/app.
        // Prompt-batch threads: the full physical estimate (prompt processing parallelizes well). Explicit overrides win.
        var threads = _options.CpuThreadCount is { } explicitThreads && explicitThreads > 0
            ? explicitThreads
            : Math.Max(physical - _options.CpuThreadReserve, 1);
        var threadsBatch = _options.CpuThreadsBatchCount is { } explicitBatch && explicitBatch > 0
            ? explicitBatch
            : physical;

        return new CpuThreadPlan(threads, threadsBatch);
    }

    /// <summary>The llama-server generation (<c>-t</c>) and prompt-batch (<c>-tb</c>) thread counts; both null on a GPU build.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct CpuThreadPlan(int? Threads, int? ThreadsBatch);
}
