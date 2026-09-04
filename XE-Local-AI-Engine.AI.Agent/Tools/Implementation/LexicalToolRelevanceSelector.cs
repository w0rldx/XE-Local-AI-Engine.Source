namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Frozen;
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
///         function words are dropped from both sides (<see cref="StopWords" />), so a description cannot win a slot
///         for containing "a", "to" or "then"; and the overlap is divided by the square root of the candidate's token
///         count, so a long description cannot win by sheer volume. The divisor is what makes the score a
///         <see langword="double" /> rather than a count; it is computed the same way on every run, so the ordering
///         stays bit-for-bit reproducible.
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
    /// <summary>
    ///     Function words — English and German articles, prepositions, conjunctions, pronouns and auxiliaries — carry no
    ///     retrieval signal but occur in nearly every tool description, so a raw overlap count is dominated by them.
    ///     Dropped from BOTH the query and the candidate: from the query so they can never match, and from the candidate
    ///     so they do not inflate the length divisor of a description that is merely wordy. Uppercase, because
    ///     <see cref="Tokenize" /> normalises before this set is consulted.
    /// </summary>
    private static readonly FrozenSet<string> StopWords = new[]
    {
        // English
        "A", "ABOUT", "ALL", "ALSO", "AN", "AND", "ANY", "ARE", "AS", "AT", "BE", "BEEN", "BEING", "BUT", "BY", "CAN",
        "COULD", "DID", "DO", "DOES", "EACH", "ELSE", "FOR", "FROM", "HAD", "HAS", "HAVE", "HE", "HER", "HERE", "HIM",
        "HIS", "I", "IF", "IN", "INTO", "IS", "IT", "ITS", "MAY", "ME", "MIGHT", "MUST", "MY", "NO", "NOT", "OF", "ON",
        "ONLY", "ONTO", "OR", "OUR", "OVER", "SHALL", "SHE", "SHOULD", "SO", "SOME", "SUCH", "THAN", "THAT", "THE",
        "THEIR", "THEM", "THEN", "THERE", "THESE", "THEY", "THIS", "THOSE", "TO", "UNDER", "US", "VERY", "WAS", "WE",
        "WERE", "WHAT", "WHEN", "WHICH", "WHILE", "WHO", "WHOM", "WILL", "WITH", "WITHOUT", "WOULD", "YOU", "YOUR",

        // German
        "ABER", "ALS", "AM", "AUCH", "AUF", "AUS", "BEI", "DANN", "DAS", "DASS", "DEIN", "DEM", "DEN", "DER", "DES",
        "DICH", "DIE", "DIR", "DU", "DURCH", "EIN", "EINE", "EINEM", "EINEN", "EINER", "EINES", "ER", "ES", "FUER",
        "FÜR", "HABEN", "HAT", "HATTE", "ICH", "IHR", "IM", "INS", "IST", "KANN", "KOENNEN", "KÖNNEN", "MEIN", "MICH",
        "MIR", "MIT", "NACH", "NICHT", "NOCH", "NUR", "ODER", "OHNE", "SCHON", "SEIN", "SICH", "SIE", "SIND", "SOLL",
        "UEBER", "UND", "UNS", "UNTER", "ÜBER", "VOM", "VON", "WAR", "WAREN", "WERDEN", "WIE", "WIR", "WIRD", "ZU",
        "ZUM", "ZUR"
    }.ToFrozenSet(StringComparer.Ordinal);

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

        var queryTokens = Tokenize(query);

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
                              .Select(entry => (entry.Index, Score: ScoreOverlap(queryTokens, Tokenize($"{entry.Candidate.Name} {entry.Candidate.Description}"))))
                              .OrderByDescending(static scored => scored.Score)
                              .ThenBy(static scored => scored.Index)
                              .Take(rankedSlots)
                              .Select(static scored => scored.Index)
                              .ToHashSet();

        // Re-impose the INPUT order over the union (the shared step, so the embedding selector cannot diverge from it).
        return Task.FromResult(ToolRelevanceSelection.Compose(candidates, selectedNonCore));
    }

    /// <summary>
    ///     Content-word overlap, normalised by the square root of the candidate's length. Square root rather than a
    ///     plain division because a full division over-corrects: it makes a one-word name beat a three-word match in a
    ///     paragraph, which is the opposite failure. Both token sets are already stopword-filtered.
    /// </summary>
    private static double ScoreOverlap(IReadOnlySet<string> queryTokens, IReadOnlySet<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        var matches = queryTokens.Count(candidateTokens.Contains);

        return matches == 0 ? 0 : matches / Math.Sqrt(candidateTokens.Count);
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
               .Where(static token => !StopWords.Contains(token))
               .ToHashSet(StringComparer.Ordinal);
    }
}
