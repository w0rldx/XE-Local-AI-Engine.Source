namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Deterministic, model-free <see cref="IToolRelevanceSelector" /> — the shipped default, and the fallback every
///     other implementation degrades to. Scores each non-core candidate by token overlap between the query and
///     <c>name + " " + description</c>, in the same shape as <c>LexicalPlaybookRetrievalRanker</c>: uppercase-normalise
///     (CA1308-safe), split on non-alphanumeric runs, compare ordinally. Ties — including the all-zero case — break by
///     the candidate's INDEX in the input list, so the outcome is reproducible with no dependence on a model or on
///     external state, and CI stays deterministic without an embedding process.
///     <para>
///         Two corrections to that raw shape, both forced by the C3 live round (2026-09-03), where "Convert 100 euros
///         to dollars, then give me a stock quote" hid the ONE tool that could answer and offered four that could not:
///         function words are dropped from both sides, so a description cannot win a slot for containing "a", "to" or
///         "then"; and the overlap is divided by the square root of the candidate's token count, so a long description
///         cannot win by sheer volume. The divisor is what makes the score a <see langword="double" /> rather than a
///         count; it is computed the same way on every run, so the ordering stays bit-for-bit reproducible. Both rules
///         live in <see cref="LexicalOverlapScoring" />, shared with the playbook ranker so the two cannot drift.
///     </para>
///     <para>
///         The core set is never ranked and never trimmed, and the fill is floored at
///         <see cref="ToolRelevanceOptions.MinimumRankedSlots" />, so a skills-heavy agent whose core alone approaches
///         the threshold still gets a meaningful set to choose among. The offered array may therefore exceed the
///         threshold: the threshold triggers filtering, <c>core + rankedSlots</c> caps it.
///     </para>
/// </summary>
public sealed class LexicalToolRelevanceSelector : IToolRelevanceSelector
{
    private readonly int _minimumRankedSlots;

    /// <summary>
    ///     Constructs the selector. <paramref name="options" /> is optional so the pipeline's defensive, re-entrant
    ///     resolution (<c>GetService&lt;IToolRelevanceSelector&gt;() ?? new LexicalToolRelevanceSelector()</c>) can
    ///     always fall back to the pinned defaults rather than throwing during a partial re-decoration.
    /// </summary>
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
        // is never touched — the byte-identical case. A query that is blank, and a query that is nothing but function
        // words ("what about it, then?"), are the same case: every score would be zero and the "ranking" would be the
        // input order, which is a worse answer than simply offering everything.
        if (candidates.Count <= threshold || queryTokens.Count == 0)
        {
            return Task.FromResult(new ToolRelevanceSelection([.. candidates.Select(static candidate => candidate.Name)], []));
        }

        var coreCount = candidates.Count(static candidate => candidate.IsCore);
        var rankedSlots = Math.Max(threshold - coreCount, _minimumRankedSlots);

        var selectedNonCore = candidates
                              .Select(static (candidate, index) => (Candidate: candidate, Index: index))
                              .Where(static entry => !entry.Candidate.IsCore)
                              .Select(entry => (entry.Index, Score: LexicalOverlapScoring.ScoreOverlap(queryTokens, LexicalOverlapScoring.Tokenize($"{entry.Candidate.Name} {entry.Candidate.Description}"))))
                              .OrderByDescending(static scored => scored.Score)
                              .ThenBy(static scored => scored.Index)
                              .Take(rankedSlots)
                              .Select(static scored => scored.Index)
                              .ToHashSet();

        // Re-impose the INPUT order over the union (the shared step, so the embedding selector cannot diverge from it).
        return Task.FromResult(ToolRelevanceSelection.Compose(candidates, selectedNonCore));
    }
}
