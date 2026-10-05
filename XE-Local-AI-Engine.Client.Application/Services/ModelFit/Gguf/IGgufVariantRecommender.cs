namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Annotates a repo's selectable GGUF files with a quality tier, a hardware fit verdict (from a single live
///     free-VRAM probe), and exactly one recommended variant, so the download picker leads with a sensible default, not
///     the smallest file.
/// </summary>
/// <remarks>Read-time only; never persists, and never throws for an absent probe or backend.</remarks>
public interface IGgufVariantRecommender
{
    /// <summary>
    ///     Returns one <see cref="GgufVariantAnnotation" /> per input file, in the same order; an empty list for an
    ///     empty input.
    /// </summary>
    /// <remarks>
    ///     Probes free VRAM once for the active backend; when it is unknown every verdict is
    ///     <see cref="GgufFitVerdict.Unknown" /> and the recommended variant falls back to the quality sweet-spot.
    ///     Never throws except on <paramref name="ct" /> cancellation.
    /// </remarks>
    Task<IReadOnlyList<GgufVariantAnnotation>> AnnotateAsync(IReadOnlyList<GgufRepoFile> files, CancellationToken ct);

    /// <summary>Returns one fit verdict per size, in the same order, graded against the memoized effective hardware profile.</summary>
    /// <remarks>
    ///     The advisor's budget (free VRAM less the knowledge companions' reserve, or available RAM in CPU mode) with the same headroom
    ///     rule as <see cref="AnnotateAsync" />. Never computes the device audit: no process start, no network. Every verdict is
    ///     <see cref="GgufFitVerdict.Unknown" /> until a determinate audit is cached, or for unknown hardware. Never throws except on
    ///     <paramref name="ct" /> cancellation.
    /// </remarks>
    Task<IReadOnlyList<GgufFitVerdict>> ClassifyAgainstProfileAsync(IReadOnlyList<long> sizesBytes, CancellationToken ct);
}
