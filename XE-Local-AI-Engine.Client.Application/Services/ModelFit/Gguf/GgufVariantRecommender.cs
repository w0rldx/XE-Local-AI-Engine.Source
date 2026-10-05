namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IGgufVariantRecommender" />: grades each file's quality tier and fit verdict and flags a
///     single recommended variant. Stateless (singletons only), so registered as a singleton.
/// </summary>
/// <remarks>
///     Resolves the active llama.cpp backend the same way the inference profiler does
///     (<see cref="IGpuVariantSelector" /> → <see cref="InferenceBackends.FromVariant" />) and probes the llama.cpp
///     process-local VRAM budget once via <see cref="IProcessVramBudgetProbe" />. Without a VRAM budget a CPU-mode node is
///     graded against <see cref="MemoryFitEstimator.ResolveFitBudgetBytes" /> (available RAM); otherwise, or on a probe
///     failure, the verdict degrades to "unknown" rather than throwing.
/// </remarks>
public sealed class GgufVariantRecommender : IGgufVariantRecommender
{
    // The inspect path skips the per-file header read, so only the on-disk size (≈ weights) is known while resident VRAM
    // also needs the KV cache and runtime overhead: mirror MemoryFitEstimator's ~12% margin + ~0.75 GiB overhead, rounded up.
    private const double HeadroomFraction = 0.15d; // Not derived from ModelFitSafetyMarginPercent: that cannot reproduce max(15 %, 1 GiB).
    private const long MinHeadroomBytes = 1024L * 1024 * 1024; // ~1 GiB floor for fixed KV/runtime overhead.

    // The unquantized float formats GgufQuantQuality grades NearLossless: the ladder's F32/F16 plus its float aliases.
    private static readonly HashSet<string> UnquantizedFloatQuants = new(["F32", "F16", "FP32", "FP16", "BF16", "F64"], StringComparer.OrdinalIgnoreCase);

    private readonly IKnowledgeCompanionReserve _companionReserve;
    private readonly ILogger<GgufVariantRecommender> _logger;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;
    private readonly IRuntimeDeviceAudit _runtimeAudit;
    private readonly IGpuVariantSelector _variantSelector;

    public GgufVariantRecommender(IGpuVariantSelector variantSelector,
        IProcessVramBudgetProbe processVramBudgetProbe,
        IKnowledgeCompanionReserve companionReserve,
        IRuntimeDeviceAudit runtimeAudit,
        ILogger<GgufVariantRecommender> logger)
    {
        _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));
        _companionReserve = companionReserve ?? throw new ArgumentNullException(nameof(companionReserve));
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

        var budgetBytes = await TryResolveFitBudgetAsync(ct);

        var tiers = new GgufQuantTier[files.Count];
        var verdicts = new GgufFitVerdict[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            tiers[i] = GgufQuantQuality.Classify(files[i].Quant);
            verdicts[i] = ClassifyFit(files[i].SizeBytes, budgetBytes);
        }

        // A speculative-decoding drafter is never THE recommended variant: it is a companion to the base weights, not a
        // usable chat model, and being the smallest high-quality-looking file in the repo it would otherwise win outright.
        var recommendedIndex = PickRecommendedIndex(files, tiers, verdicts);

        var annotations = new GgufVariantAnnotation[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            annotations[i] = new GgufVariantAnnotation
            {
                FileName = files[i].FileName,
                QualityTier = tiers[i],
                FitVerdict = verdicts[i],
                IsRecommended = i == recommendedIndex
            };
        }

        return annotations;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GgufFitVerdict>> ClassifyAgainstProfileAsync(IReadOnlyList<long> sizesBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sizesBytes);

        if (sizesBytes.Count == 0)
        {
            return [];
        }

        var budgetBytes = await TryResolveProfileBudgetAsync(ct);
        return [.. sizesBytes.Select(size => ClassifyFit(size, budgetBytes))];
    }

    // The advisor's budget from the memoized effective profile: the companion reserve comes off a GPU budget only, never off RAM.
    // Any non-cancellation failure degrades to "unknown" (null), so a page read never fails over it.
    private async Task<long?> TryResolveProfileBudgetAsync(CancellationToken ct)
    {
        try
        {
            // Never GetEffectiveProfileAsync here: on a cold or stale audit it would run the device probe (up to 15 s) inside a page read.
            if (await _runtimeAudit.PeekEffectiveProfileAsync(ct) is not { } profile)
            {
                return null;
            }

            var budget = MemoryFitEstimator.ResolveFitBudgetBytes(profile);
            if (budget <= 0)
            {
                return null;
            }

            if (!MemoryFitEstimator.UsesGpuBudget(profile))
            {
                return budget;
            }

            var reserve = await _companionReserve.ResolveGpuBytesAsync(profile, ct);
            return Math.Max(1, budget - reserve);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Hardware-profile budget could not be resolved for a GGUF fit verdict; treating the budget as unknown.");
            return null;
        }
    }

    // Resolve the active backend exactly as the inference profiler does, then probe the process-local budget once, less the knowledge companions' reserve.
    // Any non-cancellation failure degrades to "unknown" (null) — the picker must never 500 over a missing GPU/probe.
    private async Task<long?> TryResolveFitBudgetAsync(CancellationToken ct)
    {
        try
        {
            var variant = await _variantSelector.SelectVariantAsync(ct);
            var backend = InferenceBackends.FromVariant(variant);
            var budget = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(backend, ct);
            if (budget is null)
            {
                // No VRAM budget: a CPU-mode profile is graded against the advisor's RAM budget, with no companion GPU reserve.
                var cpuProfile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
                return MemoryFitEstimator.UsesGpuBudget(cpuProfile) || cpuProfile.AvailableRamBytes is not > 0
                    ? null
                    : MemoryFitEstimator.ResolveFitBudgetBytes(cpuProfile);
            }

            if (budget is not > 0)
            {
                return budget;
            }

            // The probe's figure is a live free measurement (llama.cpp's own device query), so it stands in as the profile's measured free VRAM.
            var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
            var reserve = await _companionReserve.ResolveGpuBytesAsync(profile with
            {
                AvailableVramBytes = budget
            }, ct);
            return Math.Max(1, budget.Value - reserve);
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

    private static GgufFitVerdict ClassifyFit(long sizeBytes, long? budgetBytes)
    {
        if (budgetBytes is not { } free)
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

    /// <summary>
    ///     Picks exactly one recommended variant from the repo's files (guaranteed non-empty here), or <c>-1</c> when
    ///     the repo lists nothing BUT speculative-decoding drafters.
    /// </summary>
    /// <remarks>
    ///     When some files fit, the highest quality tier among them wins, ties broken by larger size; otherwise the
    ///     best Tight file by the same order. When the budget (VRAM, or RAM in CPU mode) is known but nothing fits, the
    ///     smallest file wins; when it is unknown, a SweetSpot file, then a Balanced one, then the median. Drafters never
    ///     compete; neither do F32/F16/BF16 files while a quantized file remains, since they tie Q8_0's tier and would
    ///     win on size at 2-4x the download.
    /// </remarks>
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

        var quantized = selectable.Where(i => !IsUnquantizedFloat(files[i].Quant)).ToList();
        if (quantized.Count > 0)
        {
            selectable = quantized;
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

    private static bool IsUnquantizedFloat(string? quant)
    {
        return !string.IsNullOrWhiteSpace(quant) && UnquantizedFloatQuants.Contains(GgufQuantParser.StripDynamicPrefix(quant.Trim()));
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
