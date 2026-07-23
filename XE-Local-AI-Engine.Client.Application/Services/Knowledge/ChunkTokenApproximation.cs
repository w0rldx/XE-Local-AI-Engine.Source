namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deterministic, offline, allocation-light token approximation used to SIZE knowledge-base chunks to an embedding
///     model's context window without a tokenizer or external package. A byte-pair tokenizer averages ~<see cref="CharsPerToken" />
///     characters per token for English prose, so the estimate is <c>weighted-character-count / CharsPerToken</c>; the
///     per-character weighting biases token-dense scripts (CJK/kana/Hangul/emoji) upward so a character-bounded window of
///     such text is not silently ~4x its true token count. It intentionally over- rather than under-estimates so a chunk
///     sized against it stays within the embedder's window.
/// </summary>
/// <remarks>
///     This deliberately mirrors the divisor + script weighting of the chat-budgeting
///     <c>HeuristicTokenEstimator</c> (a separate concern: chat-history budgeting operates on <c>ChatMessage</c> parts,
///     this operates on a raw chunk string), so the two are kept as small independent equivalents rather than one coupling
///     the chunker to the invocation layer. Determinism is a hard requirement: the chunker must produce identical chunks
///     for identical input on every run and machine, so this uses only integer arithmetic and fixed Unicode ranges.
/// </remarks>
internal static class ChunkTokenApproximation
{
    /// <summary>Characters per token assumed for weighted content — matches the chat-budgeting heuristic's divisor.</summary>
    internal const int CharsPerToken = 4;

    // A non-ASCII Latin-script character (accents, sharp-s, cedilla, ...) tokenizes to modestly more than the chars/4
    // English rate, so it counts as this many weighted characters — a small upward bias without over-counting European prose.
    private const int NonAsciiCharWeight = 2;

    // A CJK ideograph, kana, Hangul syllable, or emoji code unit tokenizes to roughly one-or-more tokens PER CHARACTER,
    // whereas the chars/4 divisor assumes ~0.25 token/char — a ~4x under-count. Weighting these at CharsPerToken makes the
    // estimate about 1 token/char (conservative, upper-biased) so a char-bounded window of CJK text is not silently oversized.
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

    // Code units that tokenize to ~1+ tokens each: CJK radicals/ideographs (incl. Ext-A) + kana + CJK punctuation
    // (U+2E80..U+9FFF), Hangul syllables (U+AC00..U+D7A3), CJK compatibility ideographs (U+F900..U+FAFF), half/fullwidth
    // forms (U+FF00..U+FFEF), and surrogate halves (U+D800..U+DFFF) which stand in for emoji and CJK Ext-B+ code points,
    // each surrogate counted heavy so a two-unit emoji biases upward. Latin-1/Latin-Extended accents are intentionally
    // excluded (they fall to the lighter NonAsciiCharWeight).
    // The bounds are written as explicit hex code points (never as CJK char literals): a CJK literal and its compatibility
    // clone (e.g. U+8C48 vs U+F900) are visually identical, and exactly that ambiguity once silently diverged the two
    // mirrored copies of this classification.
    private static bool IsCjkOrEmoji(char character)
    {
        return (character >= 0x2E80 && character <= 0x9FFF)
               || (character >= 0xAC00 && character <= 0xD7A3)
               || (character >= 0xF900 && character <= 0xFAFF)
               || (character >= 0xFF00 && character <= 0xFFEF)
               || (character >= 0xD800 && character <= 0xDFFF);
    }
}
