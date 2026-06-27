namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Annotates a repo's selectable GGUF files with a quality tier, a hardware fit verdict (from a single live free-VRAM
///     probe), and exactly one recommended variant — so the download picker can lead with a sensible default instead of
///     blindly choosing the smallest file. Read-time only; never persists, never throws for an absent probe/backend.
/// </summary>
public interface IGgufVariantRecommender
{
    /// <summary>
    ///     Returns one <see cref="GgufVariantAnnotation" /> per input file (same order). Probes free VRAM once for the
    ///     active backend; when it is unknown every verdict is <see cref="GgufFitVerdict.Unknown" /> and the recommended
    ///     variant falls back to the quality sweet-spot. Returns an empty list for an empty input. Never throws except on
    ///     <paramref name="ct" /> cancellation.
    /// </summary>
    Task<IReadOnlyList<GgufVariantAnnotation>> AnnotateAsync(IReadOnlyList<GgufRepoFile> files, CancellationToken ct);
}
