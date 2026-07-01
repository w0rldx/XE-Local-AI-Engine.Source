namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IRankingFusionService" />. Implements Reciprocal Rank Fusion: for each chunk, the fused score is
///     the sum, over every list the chunk appears in, of <c>1 / (k + rank)</c> where <c>rank</c> is the chunk's 1-based
///     position in that list. The rank-smoothing constant <c>k = 60</c> is the value from the original RRF paper (Cormack
///     et al., 2009); it damps the influence of top ranks so no single arm dominates the fusion. Stateless and
///     deterministic — safe as a singleton.
/// </summary>
public sealed class ReciprocalRankFusion : IRankingFusionService
{
    /// <summary>Rank-smoothing constant from the original RRF paper. A larger value flattens the contribution of top ranks.</summary>
    public const int K = 60;

    public IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);

        var scores = new Dictionary<Guid, double>();
        foreach (var list in rankedLists)
        {
            if (list is null)
            {
                continue;
            }

            for (var position = 0; position < list.Count; position++)
            {
                // Rank is 1-based, so the best entry in a list contributes the largest reciprocal.
                var rank = position + 1;
                var contribution = 1d / (K + rank);
                scores[list[position]] = scores.TryGetValue(list[position], out var running)
                    ? running + contribution
                    : contribution;
            }
        }

        return scores
               .OrderByDescending(pair => pair.Value)
               .ThenBy(pair => pair.Key)
               .Select(pair => new RankFusionEntry(pair.Key, pair.Value))
               .ToList();
    }
}
