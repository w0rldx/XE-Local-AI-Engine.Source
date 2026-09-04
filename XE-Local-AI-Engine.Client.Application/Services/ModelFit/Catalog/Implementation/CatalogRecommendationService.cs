namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ICatalogRecommendationService" />. For each catalog entry surviving the use-case + arch-tag
///     filter, inspects its GGUF repo (<see cref="IHuggingFaceGgufDiscovery.InspectRepoAsync" />, TTL-cached), walks the
///     quant ladder with <see cref="MemoryFitEstimator" /> — passing <see cref="MoeFacts" /> built from the entry's
///     curated <c>activeParamsB</c> (preferred over the header's expert fields, which are not always present on every
///     quantized file) — and keeps the highest-quality quant at or below the requested ceiling that fits. Bounded
///     concurrency + per-repo timeout + fault isolation mirror <c>ModelFitRefreshService</c>'s explore-lane inspection
///     so one slow/broken repo never fails the whole recommendation build.
/// </summary>
internal sealed class CatalogRecommendationService : ICatalogRecommendationService
{
    /// <summary>Max concurrent HF repo inspections — bounded so a refresh stays fast yet polite to Hugging Face.</summary>
    private const int MaxConcurrentInspections = 5;

    /// <summary>Per-repo inspection timeout; a stalled repo is skipped rather than blocking the whole build.</summary>
    private static readonly TimeSpan PerRepoTimeout = TimeSpan.FromSeconds(seconds: 20);

    private readonly IModelCatalogProvider _catalogProvider;
    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly MemoryFitEstimator _estimator;
    private readonly ILogger<CatalogRecommendationService> _logger;
    private readonly ILlamaCppUpdateState _updateState;

    public CatalogRecommendationService(IModelCatalogProvider catalogProvider,
        IHuggingFaceGgufDiscovery discovery,
        MemoryFitEstimator estimator,
        ILlamaCppUpdateState updateState,
        ILogger<CatalogRecommendationService> logger)
    {
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogRecommendationResult> BuildRecommendationsAsync(string? useCase,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quantCeiling);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installedKeys);

        var snapshot = await _catalogProvider.GetCatalogAsync(cancellationToken).ConfigureAwait(false);

        // Installed-else-pinned: the node's actual runtime when known, else the compiled-in pin — the same effective
        // tag the runtime-status endpoint reports as "recommended".
        var installedOrPinnedTag = _updateState.Current.InstalledTag ?? LlamaCppReleasePins.PinnedTag;

        var eligible = snapshot.Document.Models
                               .Where(entry => useCase is null || entry.UseCases.Contains(useCase, StringComparer.Ordinal))
                               .Where(entry => ModelCatalogArchGate.Supports(installedOrPinnedTag, entry.MinLlamaCppTag))
                               .ToList();

        var gate = new SemaphoreSlim(MaxConcurrentInspections, MaxConcurrentInspections);
        CatalogRecommendationCandidate?[] candidates;
        try
        {
            var evaluations = eligible
                              .Select(entry => EvaluateEntryWithGuardAsync(entry, quantCeiling, ctxTarget, profile, installedKeys, gate, cancellationToken))
                              .ToList();
            candidates = await Task.WhenAll(evaluations).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        var ordered = candidates
                      .Where(candidate => candidate is not null)
                      .Select(candidate => candidate!)
                      .OrderBy(candidate => TierRank(candidate.Entry.Tier))
                      .ThenBy(candidate => candidate.Estimate.MoeVerdict == MoeFitVerdict.FitsWithExpertOffload ? 1 : 0)
                      .ThenBy(candidate => QuantLadder.QualityRank(candidate.File.Quant))
                      // A genuine tiebreak, deliberately BELOW quant quality: it can only separate candidates whose
                      // tier, expert-offload class and quant quality are already equal, so it never trades answer
                      // quality for a cheaper cache. A candidate whose header cannot size the KV term sorts last.
                      .ThenBy(candidate => candidate.KvBytesPerTokenAtCtx ?? long.MaxValue)
                      .ThenByDescending(candidate => candidate.Entry.ReleaseDate, StringComparer.Ordinal)
                      .ThenBy(candidate => candidate.Entry.Id, StringComparer.Ordinal)
                      .ToList();

        var recommended = ordered.Where(IsRecommended).ToList();
        var canRun = ordered.Where(candidate => !IsRecommended(candidate)).ToList();

        return new CatalogRecommendationResult(recommended, canRun, snapshot);
    }

    /// <summary>
    ///     Recommended = fits at/above Q4_K_M quality with real headroom (resident or offload-labeled).
    /// </summary>
    private static bool IsRecommended(CatalogRecommendationCandidate candidate)
    {
        return QuantLadder.QualityRank(candidate.File.Quant) <= QuantLadder.QualityRank(MemoryFitEstimator.DefaultQuant)
               && candidate.Estimate.HeadroomBytes > 0;
    }

    private static int TierRank(string tier)
    {
        return tier switch
        {
            "S" => 0,
            "A" => 1,
            "B" => 2,
            _ => 3
        };
    }

    private async Task<CatalogRecommendationCandidate?> EvaluateEntryWithGuardAsync(ModelCatalogEntry entry,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var repoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            repoCts.CancelAfter(PerRepoTimeout);

            return await EvaluateEntryAsync(entry, quantCeiling, ctxTarget, profile, installedKeys, repoCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Node shutdown / caller cancellation: propagate so the build itself is cancelled, not silently truncated.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException
                                              or OperationCanceledException or JsonException or FormatException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Skipping catalog entry {EntryId} (repo inspection failed or timed out).", entry.Id);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CatalogRecommendationCandidate?> EvaluateEntryAsync(ModelCatalogEntry entry,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        var detail = await _discovery.InspectRepoAsync(entry.GgufRepo, cancellationToken).ConfigureAwait(false);

        var selected = GgufFileSelector.SelectBestFit(_estimator, detail.Files, quantCeiling, ctxTarget, profile, file => BuildMoeFacts(entry, file));
        if (selected is null)
        {
            return null;
        }

        var (file, estimate) = selected;
        var modelName = GgufModelName.Format(entry.GgufRepo, file.Quant);
        var kvQuantAdvisory = BuildKvQuantAdvisory(entry, file, ctxTarget, profile);
        var attention = BuildAttentionShape(file);
        return new CatalogRecommendationCandidate(entry,
            file,
            estimate,
            modelName,
            installedKeys.Contains(modelName),
            kvQuantAdvisory,
            BuildKvBytesPerTokenAtCtx(file, ctxTarget, attention),
            AttentionArchTag.Resolve(attention, file.AttentionHeadCount, file.AttentionHeadCountKV));
    }

    /// <summary>
    ///     KV-cache bytes per token of context at the request's target, computed at the chat launch's own
    ///     <see cref="KvCacheQuant.Q8_0" /> element size rather than at the ranking estimate's fp16 — the number answers
    ///     "what will this cost me on this node". Returns <see langword="null" /> when the header cannot size the KV
    ///     term, so an unsizeable candidate sorts last on the tiebreak instead of winning it with a zero.
    /// </summary>
    private static long? BuildKvBytesPerTokenAtCtx(GgufRepoFile file, int ctxTarget, GgufAttentionShape attention)
    {
        if (file.BlockCount is not > 0 || file.AttentionHeadCountKV is not > 0)
        {
            return null;
        }

        var footprint = MemoryFitEstimator.EstimateKvCacheFootprint(file.BlockCount.Value,
            file.AttentionHeadCountKV.Value,
            file.EmbeddingLength ?? 0,
            file.AttentionHeadCount ?? 0,
            ctxTarget,
            KvCacheQuant.Q8_0,
            attention);
        return footprint.BytesAtContext > 0 ? (long)Math.Round(footprint.BytesPerToken) : null;
    }

    /// <summary>
    ///     Computes the advisory-only <see cref="KvQuantAdvisory" /> for the already-chosen <paramref name="file" />: the
    ///     same fit estimate re-run with an 8-bit (<see cref="KvCacheQuant.Q8_0" />) KV cache. This never affects
    ///     membership or ranking — the Recommended/CanRun split is always computed from the fp16 estimate — it only
    ///     surfaces the headroom a flash-attention runtime could unlock. Returns <see langword="null" /> when any KV-sizing
    ///     header field is missing/non-positive, because <see cref="MemoryFitEstimator" /> would then compute a zero KV term
    ///     and the "savings" would be identical to fp16, making the advisory meaningless.
    /// </summary>
    private KvQuantAdvisory? BuildKvQuantAdvisory(ModelCatalogEntry entry, GgufRepoFile file, int ctxTarget, HardwareProfile profile)
    {
        if (file.BlockCount is not > 0
            || file.AttentionHeadCountKV is not > 0
            || file.EmbeddingLength is not > 0
            || file.AttentionHeadCount is not > 0)
        {
            return null;
        }

        var quantizedEstimate = _estimator.Estimate(file.Quant,
            file.ParamCount,
            file.SizeBytes,
            file.BlockCount.Value,
            file.AttentionHeadCountKV.Value,
            file.EmbeddingLength.Value,
            file.AttentionHeadCount.Value,
            ctxTarget,
            profile,
            kvCacheQuantized: false,
            BuildMoeFacts(entry, file),
            KvCacheQuant.Q8_0,
            BuildAttentionShape(file),
            QuantLadder.IsNativeFormat(file.Quant));

        // Quantized KV always requires flash attention per the ResolvedLaunchArguments contract (KV types force FlashAttn).
        return new KvQuantAdvisory(KvCacheQuant.Q8_0,
            quantizedEstimate.EstimatedBytes,
            quantizedEstimate.HeadroomBytes,
            quantizedEstimate.Fits,
            RequiresFlashAttention: true);
    }

    /// <summary>
    ///     Prefers the catalog's curated <c>activeParamsB</c> over the file header's expert fields (not every quantized
    ///     file preserves <c>expert_count</c>/<c>expert_used_count</c> metadata) — a positive sentinel expert count is
    ///     supplied when the header omits it purely to flag MoE-ness for <see cref="MoeFacts.IsMoe" />; the actual
    ///     expert-weight-share math in <see cref="MemoryFitEstimator" /> is driven by <c>ActiveParamCount</c>, not by
    ///     the sentinel.
    /// </summary>
    private static MoeFacts? BuildMoeFacts(ModelCatalogEntry entry, GgufRepoFile file)
    {
        if (!entry.Moe)
        {
            return null;
        }

        var activeParamCount = entry.ActiveParamsB is { } activeB ? (long?)(activeB * 1_000_000_000d) : null;
        var expertCount = file.ExpertCount is > 0 ? file.ExpertCount : 1;
        return new MoeFacts(activeParamCount, expertCount, file.ExpertUsedCount);
    }

    // Explicit attention geometry from the file header for the estimator: per-head key/value lengths (preferred over the
    // derived head_dim) and interleaved sliding-window facts (window + global-layer stride). Null fields leave the
    // estimator on its legacy derived-head_dim, no-SWA path.
    private static GgufAttentionShape BuildAttentionShape(GgufRepoFile file)
    {
        return new GgufAttentionShape(file.AttentionKeyLength, file.AttentionValueLength, file.SlidingWindow, file.SlidingWindowPattern,
            file.AttentionKeyLengthMla, file.AttentionValueLengthMla);
    }
}
