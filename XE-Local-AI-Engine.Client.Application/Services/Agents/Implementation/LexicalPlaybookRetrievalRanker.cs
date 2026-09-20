namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Deterministic, model-free <see cref="IPlaybookRetrievalRanker" />: it scores each candidate by content-word
///     overlap between the query and the candidate's <c>TriggerCondition</c>, or <c>Behavior</c>, and returns the
///     top-k by score descending.
/// </summary>
/// <remarks>
///     Tokenising and scoring are <see cref="LexicalOverlapScoring" />, shared with the tool-relevance selector, and
///     ties break on Priority then CreatedAtUtc, so the ordering needs no model or external state. Two rules come
///     from that scorer: function words are dropped from both sides, and the overlap is divided by the square root of
///     the candidate's token count, so a wordy trigger cannot out-volume a short exact match. There is deliberately
///     no "return everything" path for an empty query: top-k is a hard cap, so the all-zero case still orders.
/// </remarks>
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
                                                       .Select(candidate => new ScoredCandidate(candidate,
                                                           LexicalOverlapScoring.ScoreOverlap(queryTokens, LexicalOverlapScoring.Tokenize(candidate.TriggerCondition ?? candidate.Behavior))))
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
