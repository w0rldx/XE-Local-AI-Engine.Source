namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Cheap deterministic gate for optional neural reranking. Retrieval itself always runs hybrid; the reranker is
///     skipped when both arms already agree on the best chunk or when the query has spent the optional-stage budget.
/// </summary>
public static class AdaptiveRetrievalPolicy
{
    private const double OptionalStageBudgetFraction = 0.8;

    public static AdaptiveRerankDecision DecideRerank(bool adaptiveEnabled,
        bool rerankerConfigured,
        IReadOnlyList<RankFusionInput> lexical,
        IReadOnlyList<RankFusionInput> semantic,
        int candidateCount,
        TimeSpan elapsed,
        TimeSpan latencyBudget)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(semantic);

        if (!rerankerConfigured)
        {
            return new AdaptiveRerankDecision(false, AdaptiveRerankReason.NotConfigured);
        }

        if (candidateCount <= 1)
        {
            return new AdaptiveRerankDecision(false, AdaptiveRerankReason.InsufficientCandidates);
        }

        if (!adaptiveEnabled)
        {
            return new AdaptiveRerankDecision(true, AdaptiveRerankReason.Forced);
        }

        var optionalStageBudget = TimeSpan.FromTicks((long)(Math.Max(0L, latencyBudget.Ticks) * OptionalStageBudgetFraction));
        if (elapsed >= optionalStageBudget)
        {
            return new AdaptiveRerankDecision(false, AdaptiveRerankReason.LatencyBudget);
        }

        if (lexical.Count > 0 && semantic.Count > 0 && lexical[0].ChunkId == semantic[0].ChunkId)
        {
            return new AdaptiveRerankDecision(false, AdaptiveRerankReason.ArmAgreement);
        }

        return new AdaptiveRerankDecision(true, AdaptiveRerankReason.Ambiguous);
    }
}

public readonly record struct AdaptiveRerankDecision(bool ShouldRerank, AdaptiveRerankReason Reason);

public enum AdaptiveRerankReason
{
    NotConfigured,
    InsufficientCandidates,
    Forced,
    LatencyBudget,
    ArmAgreement,
    Ambiguous
}
