namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The catalog recommendation lane: ranks the curated <see cref="ModelCatalogDocument" /> entries against the
///     node's hardware for a use-case, producing the PRIMARY "Recommended" / "Can run" sections the advisor's existing
///     live-HF discovery pipeline is demoted alongside as a secondary "Explore" lane.
/// </summary>
public interface ICatalogRecommendationService
{
    /// <summary>
    ///     Filters the current catalog to entries whose <see cref="ModelCatalogEntry.UseCases" /> match
    ///     <paramref name="useCase" /> (<see langword="null" /> = no use-case filter) and whose
    ///     <see cref="ModelCatalogEntry.MinLlamaCppTag" /> the node's runtime satisfies, inspects each survivor's GGUF
    ///     repo, walks the quant ladder with <see cref="MemoryFitEstimator" /> (MoE-aware), and splits the fitting
    ///     entries into <see cref="CatalogRecommendationResult.Recommended" /> / <see cref="CatalogRecommendationResult.CanRun" />,
    ///     each ordered tier → fit class → quant quality → recency → id.
    /// </summary>
    Task<CatalogRecommendationResult> BuildRecommendationsAsync(string? useCase,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken);
}

/// <summary>
///     One catalog entry that fits the node at some quant, with the chosen file and its (fp16-KV) memory-fit estimate.
///     <see cref="KvQuantAdvisory" /> is a purely advisory second estimate — see its own docs; it never influences
///     whether this candidate appears (that is always the fp16 <see cref="Estimate" />).
/// </summary>
/// <param name="KvBytesPerTokenAtCtx">
///     What one token of context costs in KV-cache bytes at the request's context target, computed at
///     <see cref="KvCacheQuant.Q8_0" /> — the chat launch default, so the figure answers "what will this cost me on
///     this node" rather than restating the fp16 ranking estimate. <see langword="null" /> when the header cannot size
///     the KV term; such a candidate sorts LAST on the tiebreak rather than first.
/// </param>
/// <param name="AttentionArchTag">
///     The candidate's attention shape as a stable lowercase token (see <see cref="Fit.AttentionArchTag" />), for the
///     UI. Never used as a ranking input on its own.
/// </param>
public sealed record CatalogRecommendationCandidate(
    ModelCatalogEntry Entry,
    GgufRepoFile File,
    MemoryFitEstimate Estimate,
    string ModelName,
    bool IsInstalled,
    KvQuantAdvisory? KvQuantAdvisory = null,
    long? KvBytesPerTokenAtCtx = null,
    string? AttentionArchTag = null);

/// <summary>
///     Advisory-only estimate of the memory a candidate would need with an 8-bit (<see cref="KvCacheQuant.Q8_0" />)
///     KV cache instead of the default fp16 — surfaced so future UI can hint at the headroom a quantized KV cache
///     could unlock. It is NOT used for membership or ranking: the Recommended/CanRun decision is always computed from
///     the fp16 <see cref="CatalogRecommendationCandidate.Estimate" />, because the default chat launch path uses an
///     fp16 KV cache and only the optimizer replay path ever sets a quantized KV type. The savings are an ESTIMATE, not
///     a guarantee of runtime compatibility: a quantized KV cache requires a flash-attention-capable llama.cpp runtime
///     and model architecture (<see cref="RequiresFlashAttention" /> is therefore always <see langword="true" />). It is
///     emitted only when the GGUF header carries every field the KV term needs; with incomplete metadata the KV term is
///     zero, the "savings" would be nil, and no advisory is produced.
/// </summary>
/// <param name="Quant">The KV-cache quantization the advisory was computed at (always <see cref="KvCacheQuant.Q8_0" />).</param>
/// <param name="EstimatedBytes">Total estimated footprint with the quantized KV cache (lower than the fp16 estimate).</param>
/// <param name="HeadroomBytes">Scored budget minus <see cref="EstimatedBytes" /> (negative when it still would not fit).</param>
/// <param name="Fits">Whether the candidate would fit its scored budget with the quantized KV cache.</param>
/// <param name="RequiresFlashAttention">Always <see langword="true" /> — llama.cpp requires flash attention for a quantized KV cache.</param>
public sealed record KvQuantAdvisory(
    KvCacheQuant Quant,
    long EstimatedBytes,
    long HeadroomBytes,
    bool Fits,
    bool RequiresFlashAttention);

/// <summary>
///     The catalog lane's ranked output: <see cref="Recommended" /> (fits at/above Q4_K_M with headroom) and
///     <see cref="CanRun" /> (fits, but only below Q4_K_M or with negligible headroom). Both lists are already ordered; the caller does
///     not re-rank.
/// </summary>
public sealed record CatalogRecommendationResult(
    IReadOnlyList<CatalogRecommendationCandidate> Recommended,
    IReadOnlyList<CatalogRecommendationCandidate> CanRun,
    ModelCatalogSnapshot CatalogSnapshot);
