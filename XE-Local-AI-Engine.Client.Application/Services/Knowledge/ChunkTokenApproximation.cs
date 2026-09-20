namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deterministic, offline token approximation that SIZES knowledge-base chunks to an embedding model's context
///     window with no tokenizer or external package; it over- not under-estimates, so a chunk sized against it stays
///     inside the window.
/// </summary>
/// <remarks>
///     Deliberately mirrors the divisor and script weighting of the chat-budgeting <c>HeuristicTokenEstimator</c>, a
///     separate concern that budgets chat history over <c>ChatMessage</c> parts while this operates on a raw chunk
///     string; the two stay small independent equivalents rather than coupling the chunker to the invocation layer.
///     Determinism is a hard requirement — the chunker must produce identical chunks for identical input on every run
///     and machine — so this allocates nothing and uses only integer arithmetic and fixed Unicode ranges.
/// </remarks>
internal static class ChunkTokenApproximation
{
    /// <summary>
    ///     Characters per token assumed for weighted content: a byte-pair tokenizer averages about this many characters
    ///     per token for English prose, and the value matches the chat-budgeting heuristic's divisor.
    /// </summary>
    internal const int CharsPerToken = 4;

    // A non-ASCII Latin-script character (accents, sharp-s, cedilla, ...) tokenizes to modestly more than the chars/4
    // English rate, so it counts as this many weighted characters — a small upward bias without over-counting European prose.
    private const int NonAsciiCharWeight = 2;

    // A CJK ideograph, kana, Hangul syllable, or emoji code unit tokenizes to ~1+ tokens PER CHARACTER where the divisor
    // assumes ~0.25 — a ~4x under-count; CharsPerToken weighting lifts the estimate to ~1 token/char, never oversizing CJK.
    private const int CjkCharWeight = CharsPerToken;

    /// <summary>Deterministic token estimate for a whole string: weighted character count divided by <see cref="CharsPerToken" />.</summary>
    internal static int EstimateTokens(string? value)
    {
        return WeightedLength(value) / CharsPerToken;
    }

    /// <summary>Sum of the per-character weights of <paramref name="value" /> (ASCII 1, CJK/emoji heavy, other non-ASCII medium).</summary>
    internal static int WeightedLength(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var weighted = 0;
        foreach (var character in value)
        {
            weighted += WeightOf(character);
        }

        return weighted;
    }

    /// <summary>The weight a single code unit contributes to the token estimate — used to grow a window one character at a time.</summary>
    internal static int WeightOf(char character)
    {
        if (character < 128)
        {
            return 1;
        }

        return IsCjkOrEmoji(character) ? CjkCharWeight : NonAsciiCharWeight;
    }

    /// <summary>
    ///     Whether a code unit tokenizes to roughly one or more tokens on its own: CJK radicals and ideographs including
    ///     Ext-A, kana and CJK punctuation, Hangul syllables, CJK compatibility ideographs, half- and fullwidth forms,
    ///     and surrogate halves.
    /// </summary>
    /// <remarks>
    ///     Surrogate halves stand in for emoji and CJK Ext-B+ code points, each counted heavy so a two-unit emoji biases
    ///     upward. Latin-1 and Latin-Extended accents are intentionally excluded and fall to the lighter
    ///     <see cref="NonAsciiCharWeight" />. The bounds are written as explicit hex code points, never as CJK char
    ///     literals: a CJK literal and its compatibility clone (U+8C48 vs U+F900) are visually identical, and exactly
    ///     that ambiguity silently diverges the two mirrored copies of this classification.
    /// </remarks>
    private static bool IsCjkOrEmoji(char character)
    {
        return (character >= 0x2E80 && character <= 0x9FFF)
               || (character >= 0xAC00 && character <= 0xD7A3)
               || (character >= 0xF900 && character <= 0xFAFF)
               || (character >= 0xFF00 && character <= 0xFFEF)
               || (character >= 0xD800 && character <= 0xDFFF);
    }
}
