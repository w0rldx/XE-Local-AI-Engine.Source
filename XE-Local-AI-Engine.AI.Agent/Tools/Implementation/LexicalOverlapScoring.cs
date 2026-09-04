namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Frozen;

/// <summary>
///     The one lexical scoring shape shared by <see cref="LexicalToolRelevanceSelector" /> and
///     <c>LexicalPlaybookRetrievalRanker</c> (in <c>Client.Application</c>, which sees this type through
///     <c>InternalsVisibleTo</c>). Both rank free text against short candidate texts with no model and no external
///     state, so they must tokenise and score identically: uppercase-normalise (CA1308-safe), split on non-alphanumeric
///     runs, drop function words, compare ordinally, and divide the overlap by the square root of the candidate's
///     length. Two copies of this drifted once already; one copy is the fix.
/// </summary>
internal static class LexicalOverlapScoring
{
    /// <summary>
    ///     Function words — English and German articles, prepositions, conjunctions, pronouns and auxiliaries — carry no
    ///     retrieval signal but occur in nearly every candidate text, so a raw overlap count is dominated by them.
    ///     Dropped from BOTH the query and the candidate: from the query so they can never match, and from the candidate
    ///     so they do not inflate the length divisor of a text that is merely wordy. Uppercase, because
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

    /// <summary>
    ///     Content-word overlap, normalised by the square root of the candidate's length. Square root rather than a
    ///     plain division because a full division over-corrects: it makes a one-word name beat a three-word match in a
    ///     paragraph, which is the opposite failure. Both token sets are expected to come from <see cref="Tokenize" />,
    ///     so both are already stopword-filtered. Deterministic: the same inputs give the same double on every run.
    /// </summary>
    internal static double ScoreOverlap(IReadOnlySet<string> queryTokens, IReadOnlySet<string> candidateTokens)
    {
        ArgumentNullException.ThrowIfNull(queryTokens);
        ArgumentNullException.ThrowIfNull(candidateTokens);

        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        var matches = queryTokens.Count(candidateTokens.Contains);

        return matches == 0 ? 0 : matches / Math.Sqrt(candidateTokens.Count);
    }

    /// <summary>
    ///     Uppercase-normalises (CA1308-safe), splits on non-alphanumeric runs and drops <see cref="StopWords" />. A
    ///     text that is blank, or nothing but function words, yields an empty set — the caller decides what that means.
    /// </summary>
    internal static IReadOnlySet<string> Tokenize(string? text)
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
