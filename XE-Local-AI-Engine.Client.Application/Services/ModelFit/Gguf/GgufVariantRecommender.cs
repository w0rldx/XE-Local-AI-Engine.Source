namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IGgufVariantRecommender" />. Resolves the active llama.cpp backend the same way the inference
///     profiler does (<see cref="IGpuVariantSelector" /> → <see cref="InferenceBackends.FromVariant" />), probes the
///     llama.cpp process-local VRAM budget once via <see cref="IProcessVramBudgetProbe" />, then grades each file's
///     quality tier and fit verdict and flags a single recommended variant. Stateless (singletons only) → singleton.
///     Read-time computation; degrades to "unknown" rather than throwing when the backend/probe is unavailable.
/// </summary>
public sealed class GgufVariantRecommender : IGgufVariantRecommender
{
    // Runtime-headroom margin added to a file's on-disk size before calling it a comfortable fit. The inspect path is the
    // header-free fast path, so all we have is the file size (≈ weights on disk); resident VRAM also needs the KV cache
    // and the CUDA/runtime overhead on top. We approximate that unmeasured headroom as a fraction of the file size,
    // floored at a fixed minimum so a small model still reserves room for the fixed overhead. The fraction mirrors the
    // advisor's MemoryFitEstimator (~12% safety margin + ~0.75 GiB overhead) but is rounded up conservatively here
    // because we deliberately skip the per-file header read on this path.
    private const double HeadroomFraction = 0.15d;
    private const long MinHeadroomBytes = 1024L * 1024 * 1024; // ~1 GiB floor for fixed KV/runtime overhead.

    private readonly ILogger<GgufVariantRecommender> _logger;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;
    private readonly IGpuVariantSelector _variantSelector;

    public GgufVariantRecommender(IGpuVariantSelector variantSelector,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILogger<GgufVariantRecommender> logger)
    {
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _processVramBudgetProbe = processVramBudgetProbe ?? throw new ArgumentNullException(nameof(processVramBudgetProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GgufVariantAnnotation>> AnnotateAsync(IReadOnlyList<GgufRepoFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return [];
        }

        var freeVramBytes = await TryResolveFreeVramAsync(ct).ConfigureAwait(false);

        var tiers = new GgufQuantTier[files.Count];
        var verdicts = new GgufFitVerdict[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            tiers[i] = GgufQuantQuality.Classify(files[i].Quant);
            verdicts[i] = ClassifyFit(files[i].SizeBytes, freeVramBytes);
        }

        // A speculative-decoding drafter is never THE recommended variant: it is a companion to the base weights, not a
        // usable chat model, and being the smallest high-quality-looking file in the repo it would otherwise win outright.
        var recommendedIndex = PickRecommendedIndex(files, tiers, verdicts);

        var annotations = new GgufVariantAnnotation[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            annotations[i] = new GgufVariantAnnotation(files[i].FileName, tiers[i], verdicts[i], i == recommendedIndex);
        }

        return annotations;
    }

    // Resolve the active backend exactly as the inference profiler does, then probe the process-local budget once. Any
    // non-cancellation failure degrades to "unknown" (null) — the picker must never 500 over a missing GPU/probe.
    private async Task<long?> TryResolveFreeVramAsync(CancellationToken ct)
    {
        try
        {
            var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
            var backend = InferenceBackends.FromVariant(variant);
            return await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(backend, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Process-VRAM-budget probe failed during GGUF variant recommendation; treating the budget as unknown.");
            return null;
        }
    }

    private static GgufFitVerdict ClassifyFit(long sizeBytes, long? freeVramBytes)
    {
        if (freeVramBytes is not { } free)
        {
            return GgufFitVerdict.Unknown;
        }

        if (sizeBytes > free)
        {
            return GgufFitVerdict.WontFit;
        }

        var margin = Math.Max((long)(sizeBytes * HeadroomFraction), MinHeadroomBytes);
        return sizeBytes + margin <= free ? GgufFitVerdict.Fits : GgufFitVerdict.Tight;
    }

    // Picks exactly one recommended variant (files are guaranteed non-empty here). When some files fit, the highest
    // quality tier among them wins (ties broken by larger size). Otherwise the best Tight file wins by the same order.
    // When free VRAM is known but nothing fits, the smallest file wins. When VRAM is unknown (no probe ran), a SweetSpot
    // file is preferred, then a Balanced one, and failing both the median file by size is chosen.
    // Speculative-decoding drafters are excluded from every branch: a drafter is a companion of the base weights, not a
    // runnable chat model, and being both tiny and high-quality-looking it would otherwise win the fit-first ordering
    // outright. Returns -1 (no recommendation) when the repo lists nothing BUT drafters.
    private static int PickRecommendedIndex(IReadOnlyList<GgufRepoFile> files,
        IReadOnlyList<GgufQuantTier> tiers,
        IReadOnlyList<GgufFitVerdict> verdicts)
    {
        var selectable = Enumerable.Range(start: 0, files.Count)
                                   .Where(i => !GgufDraftModel.IsDraftQuant(files[i].Quant))
                                   .ToList();
        if (selectable.Count == 0)
        {
            return -1;
        }

        var fits = IndicesWith(selectable, verdicts, GgufFitVerdict.Fits);
        if (fits.Count > 0)
        {
            return BestByTierThenSize(files, tiers, fits);
        }

        var tight = IndicesWith(selectable, verdicts, GgufFitVerdict.Tight);
        if (tight.Count > 0)
        {
            return BestByTierThenSize(files, tiers, tight);
        }

        var wontFit = IndicesWith(selectable, verdicts, GgufFitVerdict.WontFit);
        if (wontFit.Count > 0)
        {
            // Nothing fits on a known GPU → the least-bad option is the smallest file.
            return wontFit.OrderBy(i => files[i].SizeBytes).First();
        }

        // No probe: every verdict is Unknown. Prefer the quality sweet-spot, then the balanced default.
        var sweetSpot = IndicesWithTier(selectable, tiers, GgufQuantTier.SweetSpot);
        if (sweetSpot.Count > 0)
        {
            return sweetSpot.OrderByDescending(i => files[i].SizeBytes).First();
        }

        var balanced = IndicesWithTier(selectable, tiers, GgufQuantTier.Balanced);
        if (balanced.Count > 0)
        {
            return balanced.OrderByDescending(i => files[i].SizeBytes).First();
        }

        // No sweet-spot/balanced file → the median by size (a conservative middle pick).
        var bySize = selectable.OrderBy(i => files[i].SizeBytes).ToList();
        return bySize[bySize.Count / 2];
    }

    private static int BestByTierThenSize(IReadOnlyList<GgufRepoFile> files,
        IReadOnlyList<GgufQuantTier> tiers,
        IReadOnlyList<int> candidates)
    {
        return candidates
               .OrderByDescending(i => (int)tiers[i])
               .ThenByDescending(i => files[i].SizeBytes)
               .First();
    }

    // Both index filters walk the pre-filtered `candidates` set (drafters already removed) rather than every file, so
    // no branch can reintroduce a drafter.
    private static List<int> IndicesWith(IReadOnlyList<int> candidates, IReadOnlyList<GgufFitVerdict> verdicts, GgufFitVerdict verdict)
    {
        return [.. candidates.Where(i => verdicts[i] == verdict)];
    }

    private static List<int> IndicesWithTier(IReadOnlyList<int> candidates, IReadOnlyList<GgufQuantTier> tiers, GgufQuantTier tier)
    {
        return [.. candidates.Where(i => tiers[i] == tier)];
    }
}
