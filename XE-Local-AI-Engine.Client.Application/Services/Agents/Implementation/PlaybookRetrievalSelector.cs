namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The single, shared relevance retrieval and cohort monitoring relevance-retrieval decision. Both the single-agent
///     <see cref="AgentDefinitionResolver" /> and the per-participant <see cref="OrchestrationResolver" /> route through
///     this helper so the threshold gate, the top-k selection, and the deterministic re-order are applied identically and
///     never duplicated. Below the threshold (or with a blank query) the caller's full Enabled set is returned unchanged,
///     so the composed prompt — and thus the runtime config hash — stays byte-identical to the pre-retrieval static prepend.
/// </summary>
internal static class PlaybookRetrievalSelector
{
    /// <summary>
    ///     Chooses the subset of <paramref name="enabled" /> to inject for one send. When the set is at or below
    ///     <paramref name="retrievalThreshold" /> or <paramref name="retrievalQuery" /> is blank, the set is returned as-is
    ///     (static prepend, byte-identical to the pre-retrieval path) WITHOUT awaiting or invoking the ranker — so the no-op fast path
    ///     never constructs an embedding client. Otherwise the <paramref name="ranker" /> selects the top
    ///     <paramref name="topK" />, which are then re-ordered by Priority then CreatedAtUtc to preserve the composer's
    ///     deterministic store-order contract (the composer never re-sorts).
    /// </summary>
    public static async Task<IReadOnlyList<PlaybookActionRecord>> SelectAsync(IPlaybookRetrievalRanker ranker,
        string? retrievalQuery,
        IReadOnlyList<PlaybookActionRecord> enabled,
        int retrievalThreshold,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ranker);
        ArgumentNullException.ThrowIfNull(enabled);

        if (enabled.Count <= retrievalThreshold || string.IsNullOrWhiteSpace(retrievalQuery))
        {
            return enabled;
        }

        var selected = await ranker.SelectTopKAsync(retrievalQuery, enabled, topK, cancellationToken).ConfigureAwait(false);

        // The ranker orders by relevance; re-impose the store's Priority-then-CreatedAtUtc order so the composer's
        // deterministic contract holds regardless of the ranker's internal ordering.
        return selected
               .OrderBy(static action => action.Priority)
               .ThenBy(static action => action.CreatedAtUtc)
               .ToList();
    }
}
