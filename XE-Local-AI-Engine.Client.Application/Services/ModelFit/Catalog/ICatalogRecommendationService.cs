namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Ranks the curated <see cref="ModelCatalogDocument" /> entries against the node's hardware for a use-case,
///     producing the PRIMARY "Recommended" / "Can run" sections. The live-HF discovery pipeline runs alongside it as
///     the secondary "Explore" lane.
/// </summary>
public interface ICatalogRecommendationService
{
    /// <summary>
    ///     Filters the catalog to entries whose <see cref="ModelCatalogEntry.UseCases" /> match
    ///     <paramref name="useCase" /> (<see langword="null" /> = no filter) and whose
    ///     <see cref="ModelCatalogEntry.MinLlamaCppTag" /> the node's runtime satisfies, then walks each survivor's
    ///     GGUF repo down the quant ladder with <see cref="MemoryFitEstimator" />.
    /// </summary>
    /// <remarks>
    ///     The ladder walk is MoE-aware. Fitting entries split into
    ///     <see cref="CatalogRecommendationResult.Recommended" /> / <see cref="CatalogRecommendationResult.CanRun" />,
    ///     each ordered tier -> fit class -> quant quality -> recency -> id. See docs/wiki/07-model-fit.md
    ///     ("The curated catalog lane (primary recommendation source)").
    /// </remarks>
    Task<CatalogRecommendationResult> BuildRecommendationsAsync(string? useCase,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken);
}

/// <summary>
///     One catalog entry that fits the node at some quant, with the chosen file and its (fp16-KV) memory-fit estimate.
/// </summary>
/// <remarks>
///     <see cref="KvQuantAdvisory" /> is a second, purely advisory estimate: whether this candidate appears at all is
///     always decided by the fp16 <see cref="Estimate" />.
/// </remarks>
public sealed class CatalogRecommendationCandidate
{
    public required ModelCatalogEntry Entry { get; init; }

    public required GgufRepoFile File { get; init; }

    public required MemoryFitEstimate Estimate { get; init; }

    public required string ModelName { get; init; }

    public required bool IsInstalled { get; init; }

    public KvQuantAdvisory? KvQuantAdvisory { get; init; }

    /// <summary>
    ///     What one token of context costs in KV-cache bytes at the request's context target, computed at
    ///     <see cref="KvCacheQuant.Q8_0" /> — the chat launch default, so the figure answers what this model costs on
    ///     this node rather than restating the fp16 ranking estimate.
    /// </summary>
    /// <value>
    ///     <see langword="null" /> when the header cannot size the KV term; such a candidate sorts LAST on the
    ///     tiebreak rather than first.
    /// </value>
    public long? KvBytesPerTokenAtCtx { get; init; }

    /// <summary>
    ///     The candidate's attention shape as a stable lowercase token (see <see cref="Fit.AttentionArchTag" />), for the
    ///     UI. Never used as a ranking input on its own.
    /// </summary>
    public string? AttentionArchTag { get; init; }
}

/// <summary>
///     Advisory-only estimate of the memory a candidate would need with an 8-bit
///     (<see cref="KvCacheQuant.Q8_0" />) KV cache instead of the default fp16, so the UI can hint at the headroom a
///     quantized KV cache could unlock.
/// </summary>
/// <remarks>
///     It never decides membership or ranking: that is always the fp16 <see cref="CatalogRecommendationCandidate.Estimate" />,
///     because the chat launch uses an fp16 KV cache and only the optimizer replay path sets a quantized KV type. The
///     savings are an ESTIMATE, not a compatibility guarantee — a quantized KV cache needs a flash-attention-capable
///     llama.cpp runtime and architecture, so <see cref="RequiresFlashAttention" /> is always <see langword="true" />.
///     Emitted only when the header carries every KV-sizing field; incomplete metadata means a zero KV term, nil savings.
/// </remarks>
public sealed class KvQuantAdvisory
{
    /// <summary>The KV-cache quantization the advisory was computed at (always <see cref="KvCacheQuant.Q8_0" />).</summary>
    public required KvCacheQuant Quant { get; init; }

    /// <summary>Total estimated footprint with the quantized KV cache (lower than the fp16 estimate).</summary>
    public required long EstimatedBytes { get; init; }

    /// <summary>Scored budget minus <see cref="EstimatedBytes" /> (negative when it still would not fit).</summary>
    public required long HeadroomBytes { get; init; }

    /// <summary>Whether the candidate would fit its scored budget with the quantized KV cache.</summary>
    public required bool Fits { get; init; }

    /// <summary>Always <see langword="true" /> — llama.cpp requires flash attention for a quantized KV cache.</summary>
    public required bool RequiresFlashAttention { get; init; }
}

/// <summary>
///     The catalog lane's ranked output: <see cref="Recommended" /> (fits at/above Q4_K_M with headroom) and
///     <see cref="CanRun" /> (fits, but only below Q4_K_M or with negligible headroom). Both lists are already ordered; the caller does
///     not re-rank.
/// </summary>
public sealed class CatalogRecommendationResult
{
    public required IReadOnlyList<CatalogRecommendationCandidate> Recommended { get; init; }

    public required IReadOnlyList<CatalogRecommendationCandidate> CanRun { get; init; }

    public required ModelCatalogSnapshot CatalogSnapshot { get; init; }
}
