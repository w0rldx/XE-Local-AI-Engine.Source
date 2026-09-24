namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Deterministic, model-free <see cref="IToolRelevanceSelector" /> — the shipped default, and the fallback every
///     other implementation degrades to.
/// </summary>
/// <remarks>
///     Scores each non-core candidate by token overlap between the query and <c>name + " " + description</c> through
///     <see cref="LexicalOverlapScoring" />, shared with the playbook ranker so the two cannot drift. Ties, the
///     all-zero case included, break by the candidate's INDEX, so the outcome needs no model and no external state. The
///     core set is never ranked, and the fill is floored at <see cref="ToolRelevanceOptions.MinimumRankedSlots" />. See
///     docs/wiki/04-agent-mode.md ("The lexical ranker and the `list_tools` escape hatch").
/// </remarks>
public sealed class LexicalToolRelevanceSelector : IToolRelevanceSelector
{
    private readonly int _minimumRankedSlots;

    /// <summary>Constructs the selector.</summary>
    /// <remarks>
    ///     <paramref name="options" /> is optional so the pipeline's defensive, re-entrant resolution can always fall
    ///     back to the pinned defaults rather than throwing during a partial re-decoration.
    /// </remarks>
    public LexicalToolRelevanceSelector(IOptions<ToolRelevanceOptions>? options = null)
    {
        _minimumRankedSlots = options?.Value.MinimumRankedSlots ?? new ToolRelevanceOptions().MinimumRankedSlots;
    }

    /// <inheritdoc />
    public Task<ToolRelevanceSelection> SelectAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        var queryTokens = LexicalOverlapScoring.Tokenize(query);

        // Fast path: at or below the threshold, or with nothing to rank WITH, the whole array is offered and the ranker
        // is never touched. A blank query and an all-function-word query are the same case — every score is zero.
        if (candidates.Count <= threshold || queryTokens.Count == 0)
        {
            return Task.FromResult(new ToolRelevanceSelection
            {
                OfferedNames = [.. candidates.Select(static candidate => candidate.Name)],
                HiddenNames = []
            });
        }

        var coreCount = candidates.Count(static candidate => candidate.IsCore);
        var rankedSlots = Math.Max(threshold - coreCount, _minimumRankedSlots);

        var selectedNonCore = candidates
                              .Select(static (candidate, index) => (Candidate: candidate, Index: index))
                              .Where(static entry => !entry.Candidate.IsCore)
                              .Select(entry => (entry.Index,
                                  Score: LexicalOverlapScoring.ScoreOverlap(queryTokens, LexicalOverlapScoring.Tokenize($"{entry.Candidate.Name} {entry.Candidate.Description}"))))
                              .OrderByDescending(static scored => scored.Score)
                              .ThenBy(static scored => scored.Index)
                              .Take(rankedSlots)
                              .Select(static scored => scored.Index)
                              .ToHashSet();

        // Re-impose the INPUT order over the union (the shared step, so the embedding selector cannot diverge from it).
        return Task.FromResult(ToolRelevanceSelection.Compose(candidates, selectedNonCore));
    }
}
