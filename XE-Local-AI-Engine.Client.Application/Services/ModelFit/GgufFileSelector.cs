namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Shared GGUF-variant ranking core for the advisor's two selection lanes (the explore-lane
///     <c>ModelFitRefreshService</c> and the catalog-lane <c>CatalogRecommendationService</c>), which previously
///     reimplemented this ~50-line algorithm near-verbatim. The one real difference between the lanes is how
///     <see cref="MoeFacts" /> is built per file — the catalog lane derives it from a curated
///     <c>ModelCatalogEntry</c>, the explore lane has no such entry — so that is the single seam left as a
///     caller-supplied delegate; everything else (attention-shape derivation, fit filtering, native-format guard,
///     ceiling/floor ranking) is identical and lives here.
/// </summary>
internal static class GgufFileSelector
{
    /// <summary>
    ///     Walks <paramref name="files" /> against the <see cref="QuantLadder" />: estimates every file, keeps only the
    ///     ones that fit the budget, have a computable weights term, and sit at or above the quality floor, then
    ///     returns the highest quality one that does not exceed the requested <paramref name="quant" /> ceiling. When
    ///     the only fitting files are higher quality than the ceiling (a roomy box with no file at/below the target
    ///     quant) it returns the smallest fitting one so the repo still surfaces. Returns <see langword="null" /> when
    ///     nothing at or above the floor fits.
    /// </summary>
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

        // Prefer the highest quality at or below the requested ceiling (rank >= ceilingRank == quality <= ceiling). If every
        // fitting file is higher quality than the ceiling, fall back to the smallest fitting (highest rank) so the repo is
        // still recommended at a runnable quant.
        // Estimated footprint is an explicit tie-break when two files share a rank (e.g. two off-ladder labels both map to
        // the unknown rank, or a repo lists a quant twice) so the pick is deterministic regardless of file order.
        var atOrBelowCeiling = guarded.Where(candidate => candidate.rank >= ceilingRank).ToList();
        var chosen = atOrBelowCeiling.Count > 0
            ? atOrBelowCeiling.OrderBy(candidate => candidate.rank).ThenBy(candidate => candidate.estimate.EstimatedBytes).First()
            : guarded.OrderByDescending(candidate => candidate.rank).ThenBy(candidate => candidate.estimate.EstimatedBytes).First();

        return new SelectedGgufFile(chosen.file, chosen.estimate);
    }

    private static GgufAttentionShape BuildAttentionShape(GgufRepoFile file)
    {
        return new GgufAttentionShape(file.AttentionKeyLength, file.AttentionValueLength, file.SlidingWindow, file.SlidingWindowPattern,
            file.AttentionKeyLengthMla, file.AttentionValueLengthMla);
    }
}

/// <summary>The GGUF variant the ladder walk picked for a repo, together with the fit estimate it was picked on.</summary>
internal sealed record SelectedGgufFile(GgufRepoFile File, MemoryFitEstimate Estimate);
