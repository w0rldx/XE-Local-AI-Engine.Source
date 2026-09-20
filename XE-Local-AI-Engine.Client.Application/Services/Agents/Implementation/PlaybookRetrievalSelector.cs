namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The single, shared playbook relevance-retrieval decision.
/// </summary>
/// <remarks>
///     Both <see cref="AgentDefinitionResolver" /> and the per-participant <see cref="OrchestrationResolver" /> route
///     through it, so the threshold gate, the top-k selection, the token-budget trim and the deterministic re-order
///     are applied identically and never duplicated. Below the threshold, or with a blank query, the caller's full
///     Enabled set comes back unchanged, keeping prompt and config hash byte-identical to the static prepend.
/// </remarks>
internal static class PlaybookRetrievalSelector
{
    /// <summary>
    ///     Chooses the subset of <paramref name="enabled" /> to inject for one send.
    /// </summary>
    /// <remarks>
    ///     At or below <paramref name="retrievalThreshold" />, or with a blank <paramref name="retrievalQuery" />, the
    ///     set comes back as-is WITHOUT invoking the ranker, so the fast path never constructs an embedding client.
    ///     Otherwise the <paramref name="ranker" /> takes the top <paramref name="topK" />, the result is trimmed to
    ///     the token budgets lowest-ranked first, and is re-ordered by Priority then CreatedAtUtc for the composer's
    ///     store-order contract. The trim engages ONLY here, so the fast path stays byte-identical to any budget.
    /// </remarks>
    /// <param name="maxInjectedMemoryTokens">Soft total token budget for the injected memory; <c>0</c> is unbounded.</param>
    /// <param name="maxInjectedFailureMemoryTokens">Soft sub-budget for Failure-scope memory; <c>0</c> is no cap.</param>
    /// <param name="logger">Optional logger for the text-free "trimmed N" warning; <c>null</c> suppresses it.</param>
    public static async Task<IReadOnlyList<PlaybookActionRecord>> SelectAsync(IPlaybookRetrievalRanker ranker,
        string? retrievalQuery,
        IReadOnlyList<PlaybookActionRecord> enabled,
        int retrievalThreshold,
        int topK,
        CancellationToken cancellationToken,
        int maxInjectedMemoryTokens = 0,
        int maxInjectedFailureMemoryTokens = 0,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ranker);
        ArgumentNullException.ThrowIfNull(enabled);

        if (enabled.Count <= retrievalThreshold || string.IsNullOrWhiteSpace(retrievalQuery))
        {
            return enabled;
        }

        // The ranker returns the top-k in RELEVANCE order (most relevant first). Trim to the token budget here, while the
        // relevance order is still intact, so the lowest-ranked items are dropped first; THEN re-impose the store order.
        var ranked = await ranker.SelectTopKAsync(retrievalQuery, enabled, topK, cancellationToken);

        var budgeted = TrimToBudget(ranked, maxInjectedMemoryTokens, maxInjectedFailureMemoryTokens, logger);

        // The ranker orders by relevance, so re-impose the store's Priority-then-CreatedAtUtc order: a fixed memory
        // set must compose to the same prompt text, and the same config hash, on every send.
        return budgeted
               .OrderBy(static action => action.Priority)
               .ThenBy(static action => action.CreatedAtUtc)
               .ToList();
    }

    /// <summary>
    ///     Trims a relevance-ordered selection to the soft token budgets, dropping the lowest-ranked items first.
    /// </summary>
    /// <remarks>
    ///     The Failure-scope sub-budget applies first, so negative guidance cannot crowd out positive, and the
    ///     survivors are then trimmed to the total; a non-positive budget disables that level. The estimate is
    ///     conservative and deterministic, so a fixed memory set always trims to the same surviving set — a soft
    ///     guard against prompt bloat, not a correctness property.
    /// </remarks>
    private static IReadOnlyList<PlaybookActionRecord> TrimToBudget(IReadOnlyList<PlaybookActionRecord> ranked,
        int maxInjectedMemoryTokens,
        int maxInjectedFailureMemoryTokens,
        ILogger? logger)
    {
        if (ranked.Count == 0)
        {
            return ranked;
        }

        var totalBefore = ranked.Count;

        // Stage 1 caps Failure-scope items to their sub-budget, preserving relevance order across the whole list, so
        // stage 2's total-budget trim still drops lowest-ranked first overall.
        var afterFailureCap = CapByBudget(ranked, maxInjectedFailureMemoryTokens, failureOnly: true);
        var afterTotalCap = CapByBudget(afterFailureCap, maxInjectedMemoryTokens, failureOnly: false);

        var trimmed = totalBefore - afterTotalCap.Count;
        if (trimmed > 0)
        {
            // Text-free warning: the count of dropped memories only, never any playbook/query text (mirrors the
            // embedding ranker's logging discipline).
            logger?.LogWarning("Trimmed {TrimmedCount} memories over the injected-memory token budget.", trimmed);
        }

        return afterTotalCap;
    }

    /// <summary>
    ///     Walks <paramref name="ranked" /> in relevance order, keeping each item whose running token cost stays
    ///     within <paramref name="budget" /> and dropping the LOWEST-ranked ones that overflow.
    /// </summary>
    /// <remarks>
    ///     With <paramref name="failureOnly" /> only <see cref="MemoryScope.Failure" /> items count and are dropped,
    ///     the rest passing through; otherwise every item counts and the result is a relevance-ranked prefix, so a
    ///     lower-ranked item is never kept once a higher-ranked one was dropped. A non-positive budget disables the
    ///     cap, and the first counted item is always kept, so a single oversized memory still injects.
    /// </remarks>
    private static IReadOnlyList<PlaybookActionRecord> CapByBudget(IReadOnlyList<PlaybookActionRecord> ranked,
        int budget,
        bool failureOnly)
    {
        if (budget <= 0 || ranked.Count == 0)
        {
            return ranked;
        }

        var runningTokens = 0;
        var countedKept = 0;
        var capReached = false;
        var kept = new List<PlaybookActionRecord>(ranked.Count);
        foreach (var action in ranked)
        {
            if (!failureOnly || action.MemoryScope == MemoryScope.Failure)
            {
                // Once the budget is hit every further counted item is dropped: a prefix truncation, so lowest-ranked
                // first holds deterministically. Non-counted items in the Failure-only pass still pass through.
                if (capReached)
                {
                    continue;
                }

                var cost = EstimateTokens(action);
                if (runningTokens + cost > budget && countedKept > 0)
                {
                    capReached = true;
                    continue;
                }

                runningTokens += cost;
                countedKept++;
            }

            kept.Add(action);
        }

        return kept;
    }

    /// <summary>
    ///     Conservative, deterministic token estimate for one action's injected text: <c>ceil(chars / 4)</c> over the
    ///     <c>Behavior</c> the composer emits, floored at one token for any non-empty behavior.
    /// </summary>
    /// <remarks>
    ///     A soft budget guard, not a tokenizer, so it deliberately over- rather than under-counts. Being a pure
    ///     function of the stored text it is stable across sends, which keeps the trim, the injected set and the
    ///     config hash deterministic for a fixed memory set.
    /// </remarks>
    private static int EstimateTokens(PlaybookActionRecord action)
    {
        var length = action.Behavior?.Length ?? 0;
        if (length == 0)
        {
            return 0;
        }

        return (length + 3) / 4;
    }
}
