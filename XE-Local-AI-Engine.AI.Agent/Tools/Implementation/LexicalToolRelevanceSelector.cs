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

        // Fast path: at or below the threshold, or with nothing to rank against, the whole array is offered and the
        // ranker is never touched — the byte-identical case.
        if (candidates.Count <= threshold || string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new ToolRelevanceSelection([.. candidates.Select(static candidate => candidate.Name)], []));
        }

        var coreCount = candidates.Count(static candidate => candidate.IsCore);
        var rankedSlots = Math.Max(threshold - coreCount, _minimumRankedSlots);

        var queryTokens = Tokenize(query);
        var selectedNonCore = candidates
                              .Select(static (candidate, index) => (Candidate: candidate, Index: index))
                              .Where(static entry => !entry.Candidate.IsCore)
                              .Select(entry => (entry.Index, Score: ScoreOverlap(queryTokens, Tokenize($"{entry.Candidate.Name} {entry.Candidate.Description}"))))
                              .OrderByDescending(static scored => scored.Score)
                              .ThenBy(static scored => scored.Index)
                              .Take(rankedSlots)
                              .Select(static scored => scored.Index)
                              .ToHashSet();

        // Re-impose the INPUT order over the union, so a fixed selected set always serialises to the same tools array
        // (stable prompt prefix, one GBNF compilation) regardless of the ranker's internal ordering.
        var offered = new List<string>(candidates.Count);
        var hidden = new List<string>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.IsCore || selectedNonCore.Contains(index))
            {
                offered.Add(candidate.Name);
            }
            else
            {
                hidden.Add(candidate.Name);
            }
        }

        return Task.FromResult(new ToolRelevanceSelection(offered, hidden));
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
}
