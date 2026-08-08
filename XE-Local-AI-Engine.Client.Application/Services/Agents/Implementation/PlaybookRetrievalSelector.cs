namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The single, shared relevance retrieval and cohort monitoring relevance-retrieval decision. Both the single-agent
///     <see cref="AgentDefinitionResolver" /> and the per-participant <see cref="OrchestrationResolver" /> route through
///     this helper so the threshold gate, the top-k selection, the token-budget trim, and the deterministic re-order are
///     applied identically and never duplicated. Below the threshold (or with a blank query) the caller's full Enabled set
///     is returned unchanged, so the composed prompt — and thus the runtime config hash — stays byte-identical to the
///     pre-retrieval static prepend.
/// </summary>
internal static class PlaybookRetrievalSelector
{
    /// <summary>
    ///     Chooses the subset of <paramref name="enabled" /> to inject for one send. When the set is at or below
    ///     <paramref name="retrievalThreshold" /> or <paramref name="retrievalQuery" /> is blank, the set is returned as-is
    ///     (static prepend, byte-identical to the pre-retrieval path) WITHOUT awaiting or invoking the ranker — so the no-op fast path
    ///     never constructs an embedding client. Otherwise the <paramref name="ranker" /> selects the top
    ///     <paramref name="topK" />; that relevance-ordered list is then trimmed to the token budgets (adaptive memory)
    ///     — lowest-ranked items dropped first — and finally re-ordered by Priority then CreatedAtUtc to preserve
    ///     the composer's deterministic store-order contract (the composer never re-sorts). The trim engages ONLY on this
    ///     retrieval path, so the static-prepend fast path above stays byte-identical regardless of any configured budget.
    /// </summary>
    /// <param name="maxInjectedMemoryTokens">
    ///     Soft total token budget for the injected memory; <c>0</c> = unbounded (legacy, byte-identical to pre-budget).
    /// </param>
    /// <param name="maxInjectedFailureMemoryTokens">
    ///     Soft sub-budget reserved for Failure-scope memory within the total; <c>0</c> = no separate Failure cap.
    /// </param>
    /// <param name="logger">
    ///     Optional logger for the text-free "trimmed N" warning; <c>null</c> suppresses logging (e.g. in unit tests).
    /// </param>
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
        var ranked = await ranker.SelectTopKAsync(retrievalQuery, enabled, topK, cancellationToken).ConfigureAwait(false);

        var budgeted = TrimToBudget(ranked, maxInjectedMemoryTokens, maxInjectedFailureMemoryTokens, logger);

        // The ranker orders by relevance; re-impose the store's Priority-then-CreatedAtUtc order so the composer's
        // deterministic contract holds regardless of the ranker's internal ordering (and so a fixed memory set always
        // composes to the same prompt text — and thus the same config hash — across sends, preserving resume-safety).
        return budgeted
               .OrderBy(static action => action.Priority)
               .ThenBy(static action => action.CreatedAtUtc)
               .ToList();
    }

    /// <summary>
    ///     Trims a relevance-ordered selection to the soft token budgets, dropping the lowest-ranked items first. The
    ///     Failure-scope sub-budget is applied first (so negative "what NOT to do" guidance can't crowd out positive
    ///     guidance), then the surviving items are trimmed to the total budget. A non-positive budget disables that level.
    ///     The token estimate is intentionally conservative and deterministic (see <see cref="EstimateTokens" />) so a
    ///     fixed memory set always trims to the same surviving set — the budget is a soft guard against prompt bloat, not
    ///     an exact correctness property.
    /// </summary>
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

        // Stage 1: cap Failure-scope items to their sub-budget, preserving relevance order across the whole list, so the
        // subsequent total-budget pass still drops lowest-ranked first overall. Stage 2: trim the survivors to the total
        // budget, again dropping lowest-ranked first.
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
    ///     Walks <paramref name="ranked" /> in relevance order and keeps each item whose running token cost stays within
    ///     <paramref name="budget" />, dropping the LOWEST-ranked items that overflow. When <paramref name="failureOnly" />
    ///     is true only <see cref="MemoryScope.Failure" /> items count against — and are the only ones dropped by — the
    ///     budget (non-Failure items always pass through, so the surviving Failure items are a relevance-ranked prefix of
    ///     the originals); when false every item counts and the result is a relevance-ranked prefix (a lower-ranked item
    ///     is never kept once a higher-ranked one was dropped for budget). A non-positive <paramref name="budget" />
    ///     disables the cap and returns the input unchanged. Always keeps at least the first counted item so a single
    ///     oversized memory still injects.
    /// </summary>
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
                // Once the budget is hit, every further counted (lower-ranked) item is dropped — a prefix truncation, so
                // "lowest-ranked dropped first" holds deterministically. Non-counted items (in the Failure-only pass) are
                // unaffected and continue to pass through.
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
    ///     <c>Behavior</c> string (the text the composer actually emits as a bullet), with a floor of 1 token for any
    ///     non-empty behavior. ~4 chars/token is the common English rule of thumb; this is a soft budget guard, not an
    ///     exact tokenizer, so it deliberately over- rather than under-counts. Being a pure function of the stored text it
    ///     is stable across sends, which keeps the budget trim — and therefore the injected set and the config hash —
    ///     deterministic for a fixed memory set.
    /// </summary>
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
