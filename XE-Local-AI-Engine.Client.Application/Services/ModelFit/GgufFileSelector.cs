namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Shared GGUF-variant ranking core for the advisor's two selection lanes, the explore-lane
///     <c>ModelFitRefreshService</c> and the catalog-lane <c>CatalogRecommendationService</c>.
/// </summary>
/// <remarks>
///     The lanes differ only in how <see cref="MoeFacts" /> is built per file — the catalog lane derives it from a
///     curated <c>ModelCatalogEntry</c>, the explore lane has no such entry — so that is the single seam left as a
///     caller-supplied delegate. Everything else (attention-shape derivation, fit filtering, native-format guard,
///     ceiling/floor ranking) is identical and lives here, so neither lane reimplements it.
/// </remarks>
internal static class GgufFileSelector
{
    /// <summary>
    ///     Walks <paramref name="files" /> against the <see cref="QuantLadder" /> and returns the highest-quality file
    ///     that fits the budget without exceeding the requested <paramref name="quant" /> ceiling.
    /// </summary>
    /// <remarks>
    ///     Only files that fit the budget, have a computable weights term and sit at or above the quality floor are
    ///     considered. When every fitting file is higher quality than the ceiling (a roomy box with no file at or
    ///     below the target quant) the smallest fitting one is returned so the repo still surfaces.
    /// </remarks>
    /// <returns><see langword="null" /> when nothing at or above the floor fits.</returns>
    public static SelectedGgufFile? SelectBestFit(MemoryFitEstimator estimator,
        IReadOnlyList<GgufRepoFile> files,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        Func<GgufRepoFile, MoeFacts?>? moeFactsSelector = null)
    {
        var ceilingRank = QuantLadder.QualityRank(quant);
        var floorRank = QuantLadder.FloorRank;

        var fitting = files
                      // A speculative-decoding drafter is not a candidate model: it is a companion loaded inside a chat
                      // process, and its tiny size would let it out-fit every real quant in the repo.
                      .Where(static file => !GgufDraftModel.IsDraftQuant(file.Quant))
                      .Select(file => (file, estimate: estimator.Estimate(file.Quant,
                          file.ParamCount,
                          file.SizeBytes,
                          file.BlockCount ?? 0,
                          file.AttentionHeadCountKV ?? 0,
                          file.EmbeddingLength ?? 0,
                          file.AttentionHeadCount ?? 0,
                          ctxTarget,
                          profile,
                          kvCacheQuantized: false,
                          moeFactsSelector?.Invoke(file),
                          // Explicit key/value lengths and interleaved sliding-window facts correct the KV term, and
                          // native-format detection prices a native MXFP4 quant at its own density.
                          attention: BuildAttentionShape(file),
                          nativeQuantFormat: QuantLadder.IsNativeFormat(file.Quant)), rank: QuantLadder.QualityRank(file.Quant)))
                      // Drop insufficient-metadata files (no weights term), non-fitting files, and quants below the floor.
                      .Where(candidate => candidate.estimate.EstimatedBytes > estimator.OverheadBytes
                                          && candidate.estimate.Fits
                                          && candidate.rank <= floorRank)
                      .ToList();

        if (fitting.Count == 0)
        {
            return null;
        }

        // Native-format guard: when the repo ships a native, non-requantizable format (MXFP4), the advisor must never
        // prefer a higher-nominal-quality requant of it — the native file caps the repo's recommendable quality.
        var guarded = MemoryFitEstimator.FilterOutNativeFormatRequants(fitting, candidate => candidate.file.Quant, candidate => candidate.rank);

        // Prefer the highest quality at or below the ceiling (rank >= ceilingRank); if every fitting file is higher quality
        // than it, take the smallest fitting, tie-broken by footprint so repeated/off-ladder ranks pick deterministically.
        var atOrBelowCeiling = guarded.Where(candidate => candidate.rank >= ceilingRank).ToList();
        var chosen = atOrBelowCeiling.Count > 0
            ? atOrBelowCeiling.OrderBy(candidate => candidate.rank).ThenBy(candidate => candidate.estimate.EstimatedBytes).First()
            : guarded.OrderByDescending(candidate => candidate.rank).ThenBy(candidate => candidate.estimate.EstimatedBytes).First();

        return new SelectedGgufFile(chosen.file, chosen.estimate);
    }

    private static GgufAttentionShape BuildAttentionShape(GgufRepoFile file)
    {
        return new GgufAttentionShape
        {
            KeyLength = file.AttentionKeyLength,
            ValueLength = file.AttentionValueLength,
            SlidingWindow = file.SlidingWindow,
            SlidingWindowPattern = file.SlidingWindowPattern,
            KeyLengthMla = file.AttentionKeyLengthMla,
            ValueLengthMla = file.AttentionValueLengthMla
        };
    }
}

/// <summary>The GGUF variant the ladder walk picked for a repo, together with the fit estimate it was picked on.</summary>
internal sealed record SelectedGgufFile(GgufRepoFile File, MemoryFitEstimate Estimate);
