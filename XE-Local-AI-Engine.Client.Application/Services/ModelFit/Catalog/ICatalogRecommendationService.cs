namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The catalog recommendation lane: ranks the curated <see cref="ModelCatalogDocument" /> entries against the
///     node's hardware for a use-case, producing the PRIMARY "Recommended" / "Can run" sections (locked decision D1/D2)
///     the advisor's existing live-HF discovery pipeline is demoted alongside as a secondary "Explore" lane.
/// </summary>
public interface ICatalogRecommendationService
{
    /// <summary>
    ///     Filters the current catalog to entries whose <see cref="ModelCatalogEntry.UseCases" /> match
    ///     <paramref name="useCase" /> (<see langword="null" /> = no use-case filter) and whose
    ///     <see cref="ModelCatalogEntry.MinLlamaCppTag" /> the node's runtime satisfies, inspects each survivor's GGUF
    ///     repo, walks the quant ladder with <see cref="MemoryFitEstimator" /> (MoE-aware), and splits the fitting
    ///     entries into <see cref="CatalogRecommendationResult.Recommended" /> / <see cref="CatalogRecommendationResult.CanRun" />,
    ///     each ordered tier → fit class → quant quality → recency → id (plan §7).
    /// </summary>
    Task<CatalogRecommendationResult> BuildRecommendationsAsync(string? useCase,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken);
}

/// <summary>One catalog entry that fits the node at some quant, with the chosen file and its memory-fit estimate.</summary>
public sealed record CatalogRecommendationCandidate(
    ModelCatalogEntry Entry,
    GgufRepoFile File,
    MemoryFitEstimate Estimate,
    string ModelName,
    bool IsInstalled);

/// <summary>
///     The catalog lane's ranked output: <see cref="Recommended" /> (fits at/above Q4_K_M with headroom) and
///     <see cref="CanRun" /> (fits, but only below Q4_K_M or with negligible headroom) — see plan §7. Both lists are
///     already ordered; the caller does not re-rank.
/// </summary>
public sealed record CatalogRecommendationResult(
    IReadOnlyList<CatalogRecommendationCandidate> Recommended,
    IReadOnlyList<CatalogRecommendationCandidate> CanRun,
    ModelCatalogSnapshot CatalogSnapshot);
