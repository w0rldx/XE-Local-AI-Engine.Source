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
}
