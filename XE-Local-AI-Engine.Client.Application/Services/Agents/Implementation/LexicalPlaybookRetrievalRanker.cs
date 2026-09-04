namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Deterministic, model-free <see cref="IPlaybookRetrievalRanker" />: scores each candidate by content-word overlap
///     between the query and the candidate's <c>TriggerCondition</c> (falling back to <c>Behavior</c> when no trigger is
///     set), then returns the top-k by score descending. Tokenising and scoring are
///     <see cref="LexicalOverlapScoring" />, the shape shared with the tool-relevance selector: uppercase-normalise
///     (CA1308-safe), split on non-alphanumeric runs, compare ordinally. Ties — including the blank-query case, where
///     every candidate scores zero — break by Priority ascending then CreatedAtUtc ascending, so the result is a stable,
///     reproducible ordering with no dependence on a model or external state.
///     <para>
///         Two rules inherited from that shared scorer, both forced by the C3 live round (2026-09-03): function words
///         ("the", "und", "der") are dropped from both sides, so a playbook cannot win a slot for containing them; and
///         the overlap is divided by the square root of the candidate's token count, so a wordy trigger cannot beat a
///         short exact match by sheer volume. The divisor is what makes the score a <see langword="double" /> rather
///         than a count; it is computed the same way on every run, so the ordering stays bit-for-bit reproducible.
///     </para>
///     <para>
///         There is deliberately no "return everything" fast path for a query that tokenises to nothing, unlike the tool
///         selector: top-k is a hard cap here, so the all-zero case must still produce the Priority/CreatedAtUtc order
///         rather than an arbitrary prefix of the input.
///     </para>
/// </summary>
public sealed class LexicalPlaybookRetrievalRanker : IPlaybookRetrievalRanker
{
    public Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (k <= 0 || candidates.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]);
        }

        var queryTokens = LexicalOverlapScoring.Tokenize(query);

        IReadOnlyList<PlaybookActionRecord> selected = candidates
                                                       .Select(candidate => new ScoredCandidate(candidate, LexicalOverlapScoring.ScoreOverlap(queryTokens, LexicalOverlapScoring.Tokenize(candidate.TriggerCondition ?? candidate.Behavior))))
                                                       .OrderByDescending(scored => scored.Score)
                                                       .ThenBy(scored => scored.Action.Priority)
                                                       .ThenBy(scored => scored.Action.CreatedAtUtc)
                                                       .Take(k)
                                                       .Select(scored => scored.Action)
                                                       .ToList();

        return Task.FromResult(selected);
    }

    private readonly record struct ScoredCandidate(PlaybookActionRecord Action, double Score);
}
