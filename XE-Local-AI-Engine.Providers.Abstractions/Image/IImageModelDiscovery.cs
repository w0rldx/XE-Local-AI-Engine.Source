namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>Sort order for image-model repo discovery. Mirrors the GGUF lane's ordering choices.</summary>
public enum ImageModelSearchSort
{
    /// <summary>Recency-weighted popularity (the Hub "Trending" ranking). The default.</summary>
    Trending = 0,

    /// <summary>Most downloaded first (lifetime cumulative downloads).</summary>
    Downloads = 1,

    Likes = 2,

    LastModified = 3
}

/// <summary>The container format a discovered weight file ships in. Both are loadable by stable-diffusion.cpp.</summary>
public enum ImageWeightFormat
{
    /// <summary>A <c>.gguf</c> weight file (quantized; what the diffusion parts normally ship as).</summary>
    Gguf = 0,

    /// <summary>A <c>.safetensors</c> weight file (what VAEs and several text encoders ship as).</summary>
    Safetensors = 1
}

/// <summary>A free-text image-model repo search over the Hugging Face Hub.</summary>
public sealed record ImageModelSearchQuery
{
    /// <summary>Free-text search term; when <see langword="null" /> the trending text-to-image repos are returned.</summary>
    public string? SearchText { get; init; }

    public int Limit { get; init; } = 20;

    /// <summary>Result ordering. Defaults to <see cref="ImageModelSearchSort.Trending" />.</summary>
    public ImageModelSearchSort Sort { get; init; } = ImageModelSearchSort.Trending;

    /// <summary>
    ///     Narrows the search to repos additionally tagged <c>gguf</c>. Off by default: a real file-set is routinely
    ///     assembled from a quantized GGUF diffusion repo plus a <c>.safetensors</c> VAE that carries no gguf tag, so
    ///     forcing the tag on would hide half the parts an install needs.
    /// </summary>
    public bool GgufOnly { get; init; }
}

/// <summary>
///     Summary of an image-model repo from a Hub search (popularity + gating + license).
///     <see cref="IsTrustedPublisher" /> is the same soft signal the GGUF lane uses — never an exclusion gate.
/// </summary>
public sealed record ImageRepoSummary(
    string RepoId,
    bool IsGated,
    long Downloads,
    int Likes,
    DateTimeOffset LastModified,
    string? License,
    bool HasUsableWeights,
    bool IsTrustedPublisher);

/// <summary>
///     One selectable weight file inside an image-model repo: its repo-relative name, container format, byte size and
///     the part role the file name suggests. <see cref="SuggestedRole" /> is a naming heuristic the picker pre-selects —
///     the operator can always override it, and nothing downstream trusts it as fact.
/// </summary>
public sealed record ImageRepoFile(
    string FileName,
    ImageWeightFormat Format,
    long SizeBytes,
    string? Sha256,
    ImageModelPartRole SuggestedRole);

/// <summary>One inspected image-model repo: gating/license plus every selectable weight file it ships.</summary>
public sealed record ImageRepoDetail(string RepoId, bool IsGated, string? License, IReadOnlyList<ImageRepoFile> Files);

/// <summary>
///     Queries the Hugging Face Hub for image (diffusion) model repos and inspects their weight files, so the operator
///     picks a model from a list instead of hand-typing a repo id, a file name and a family.
///     <para>
///         Deliberately NOT a copy of <see cref="Gguf.IHuggingFaceGgufDiscovery" />: that seam requires a parseable
///         llama.cpp quant token and accepts <c>.gguf</c> only, both of which are wrong here. A diffusion file-set
///         legitimately mixes <c>.gguf</c> diffusion weights with a <c>.safetensors</c> VAE, and neither a VAE nor a
///         CLIP encoder carries a quant token at all.
///     </para>
/// </summary>
public interface IImageModelDiscovery
{
    /// <summary>Searches text-to-image repos in the requested order; repos with no usable weight file are excluded.</summary>
    Task<IReadOnlyList<ImageRepoSummary>> SearchAsync(ImageModelSearchQuery query, CancellationToken ct);

    /// <summary>
    ///     Lists one repo's selectable weight files with per-file size and a suggested role. Files that are not usable
    ///     as an installable part (an unsafe path, a multi-file shard, a non-weight file) are skipped, never
    ///     repo-dropping.
    /// </summary>
    Task<ImageRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct);
}
