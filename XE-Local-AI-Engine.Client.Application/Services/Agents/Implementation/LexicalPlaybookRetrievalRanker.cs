namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Deterministic, model-free <see cref="IPlaybookRetrievalRanker" />: scores each candidate by token-overlap
///     between the query and the candidate's <c>TriggerCondition</c> (falling back to <c>Behavior</c> when no trigger is
///     set), then returns the top-k by overlap descending. Tokens are normalised by uppercasing (CA1308-safe) and
///     splitting on non-alphanumeric runs, compared with <see cref="StringComparison.Ordinal" />. Ties — including the
///     blank-query case, where every candidate scores zero — break by Priority ascending then CreatedAtUtc ascending, so
///     the result is a stable, reproducible ordering with no dependence on a model or external state.
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

        var queryTokens = Tokenize(query);

        IReadOnlyList<PlaybookActionRecord> selected = candidates
                                                       .Select(candidate => new ScoredCandidate(candidate, ScoreOverlap(queryTokens, Tokenize(candidate.TriggerCondition ?? candidate.Behavior))))
                                                       .OrderByDescending(scored => scored.Score)
                                                       .ThenBy(scored => scored.Action.Priority)
                                                       .ThenBy(scored => scored.Action.CreatedAtUtc)
                                                       .Take(k)
                                                       .Select(scored => scored.Action)
                                                       .ToList();

        return Task.FromResult(selected);
    }

    private static int ScoreOverlap(IReadOnlySet<string> queryTokens, IReadOnlySet<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(candidateTokens.Contains);
    }

    private static IReadOnlySet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var normalized = new string(text
                                    .ToUpperInvariant()
                                    .Select(static character => char.IsLetterOrDigit(character) ? character : ' ')
                                    .ToArray());

        return normalized
               .Split(separator: ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .ToHashSet(StringComparer.Ordinal);
    }

    private readonly record struct ScoredCandidate(PlaybookActionRecord Action, int Score);
}
