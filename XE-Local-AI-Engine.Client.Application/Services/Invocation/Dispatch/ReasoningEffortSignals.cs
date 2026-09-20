namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;

using System.Text;

/// <summary>
///     The deterministic ladder behind reasoning effort <c>auto</c>: the phrase vocabularies and the integer score
///     that turn one turn's SHAPE into a <see cref="ReasoningTier" />.
/// </summary>
/// <remarks>
///     Separate from the dispatcher so the rule set is table-testable without a container and the translated phrase
///     lists live in one place. Two degrees of freedom, both pinned: folding is <c>ToUpperInvariant</c> (CA1308 —
///     never fold to lower case) and every comparison is <see cref="StringComparison.Ordinal" />. The offered tool
///     count is deliberately NOT a score term: tools are on for essentially every tools-mode turn, so it cancelled
///     the short-turn signal and made <see cref="ReasoningTier.Fast" /> unreachable. Tools refuse the SWAP instead.
/// </remarks>
public static class ReasoningEffortSignals
{
    /// <summary>A fenced code block opener. Its presence is the strongest single "this is real work" signal.</summary>
    private const string CodeFence = "```";

    /// <summary>Length at which a message starts to look like a specification rather than a question.</summary>
    private const int LongMessageChars = 600;

    /// <summary>Length at which it looks like a specification with attachments to it.</summary>
    private const int VeryLongMessageChars = 1200;

    /// <summary>At or below this, with no code fence, the message is a remark rather than a task.</summary>
    private const int ShortMessageChars = 120;

    /// <summary>At or below this, a question mark early in a conversation reads as a quick lookup.</summary>
    private const int QuickQuestionChars = 200;

    /// <summary>A conversation this many messages deep has accumulated enough state to be worth reasoning over.</summary>
    private const int DeepConversationMessages = 8;

    /// <summary>Score at or above which the turn resolves to <see cref="ReasoningTier.Deep" />.</summary>
    private const int DeepScoreThreshold = 3;

    /// <summary>Score at or below which the turn resolves to <see cref="ReasoningTier.Fast" />.</summary>
    private const int FastScoreThreshold = -1;

    // Folded to upper case ONCE at class initialisation (CA1308: never normalize to lower case). Written here in
    // lower case only because that is how a reader recognizes them.
    private static readonly string[] DeepPhrasesUpper = ToUpper([
        // en
        "think harder", "think it through", "step by step", "carefully", "thoroughly", "prove", "derive",
        "root cause", "debug", "refactor", "why does",
        // de
        "denk gründlich", "schritt für schritt", "sorgfältig", "beweise", "herleiten", "ursache"
    ]);

    private static readonly string[] FastPhrasesUpper = ToUpper([
        // en
        "quick answer", "quickly", "one word", "in one sentence", "briefly", "tl;dr", "just tell me",
        // de
        "kurz", "in einem satz", "schnell", "nur kurz"
    ]);

    /// <summary>
    ///     Scores one turn and names the tier, plus the single rule that decided it. Pure: the same inputs always
    ///     produce the same answer.
    /// </summary>
    public static (ReasoningTier Tier, string ReasonCode) Resolve(string latestUserText, bool hasAttachments, int conversationDepth)
    {
        var text = latestUserText ?? string.Empty;

        var hasCodeFence = text.Contains(CodeFence, StringComparison.Ordinal);

        // Phrases are read from the PROSE only: a pasted snippet saying "debug" or "briefly" is code to look at, not an
        // instruction, and the fence already scores it. Lengths still measure the whole message — a long paste IS long.
        var upper = (hasCodeFence ? StripFencedBlocks(text) : text).ToUpperInvariant();
        var hasDeepPhrase = ContainsPhrase(upper, DeepPhrasesUpper);
        var hasFastPhrase = ContainsPhrase(upper, FastPhrasesUpper);
        var isLong = text.Length >= LongMessageChars;
        var isDeepContext = conversationDepth >= DeepConversationMessages;

        var score = 0;
        if (hasCodeFence)
        {
            score += 2;
        }

        if (hasDeepPhrase)
        {
            score += 2;
        }

        if (isLong)
        {
            score += 1;
        }

        if (text.Length >= VeryLongMessageChars)
        {
            score += 1;
        }

        // Promotes, never demotes: an image-bearing turn is not a trivially short one, and a promoting term can
        // never make FAST unreachable. (An attachment separately REFUSES the model swap.)
        if (hasAttachments)
        {
            score += 1;
        }

        if (isDeepContext)
        {
            score += 1;
        }

        if (hasFastPhrase)
        {
            score -= 2;
        }

        // Guarded on non-blank text: an empty or assistant-only conversation context gives the dispatcher NO text, and
        // "no message" is not "a short remark". Without the guard such a turn scored -1 and silently resolved to Fast.
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= ShortMessageChars && !hasCodeFence)
        {
            score -= 1;
        }

        if (text.EndsWith('?') && text.Length <= QuickQuestionChars && conversationDepth <= 2)
        {
            score -= 1;
        }

        if (score >= DeepScoreThreshold)
        {
            return (ReasoningTier.Deep, DeepReason(hasCodeFence, hasDeepPhrase, isLong, isDeepContext));
        }

        if (score <= FastScoreThreshold)
        {
            return (ReasoningTier.Fast, hasFastPhrase ? ReasoningDispatchReasons.FastPhrase : ReasoningDispatchReasons.ShortTurn);
        }

        return (ReasoningTier.Normal, ReasoningDispatchReasons.Balanced);
    }

    /// <summary>The heaviest signal that pushed the turn up, so the notice names the rule a reader can act on.</summary>
    private static string DeepReason(bool hasCodeFence, bool hasDeepPhrase, bool isLong, bool isDeepContext)
    {
        if (hasCodeFence)
        {
            return ReasoningDispatchReasons.CodeFence;
        }

        if (hasDeepPhrase)
        {
            return ReasoningDispatchReasons.DeepPhrase;
        }

        if (isLong)
        {
            return ReasoningDispatchReasons.LongMessage;
        }

        // Both arms are unreachable at the current weights (Deep needs 3; without a fence, deep phrase or long message
        // the maximum is 2). Kept so this stays total: a future weight change must not produce an empty reason.
        return isDeepContext ? ReasoningDispatchReasons.DeepContext : ReasoningDispatchReasons.Balanced;
    }

    /// <summary>
    ///     Returns the message with every fenced region removed, so a phrase inside a pasted snippet cannot move the
    ///     score.
    /// </summary>
    /// <remarks>
    ///     Walks <c>```</c> markers pairwise; an UNCLOSED fence swallows the rest of the message, the safe direction,
    ///     since everything after an opener is code until proven otherwise. Called only when a fence is present, so
    ///     an ordinary message allocates nothing.
    /// </remarks>
    private static string StripFencedBlocks(string text)
    {
        var prose = new StringBuilder(text.Length);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var opener = text.IndexOf(CodeFence, cursor, StringComparison.Ordinal);
            if (opener < 0)
            {
                _ = prose.Append(text, cursor, text.Length - cursor);
                break;
            }

            _ = prose.Append(text, cursor, opener - cursor);
            var closer = text.IndexOf(CodeFence, opener + CodeFence.Length, StringComparison.Ordinal);
            if (closer < 0)
            {
                break;
            }

            cursor = closer + CodeFence.Length;
        }

        return prose.ToString();
    }

    /// <summary>
    ///     Word-boundary containment: the phrase must not be embedded inside a longer word, so "kurz" does not fire on
    ///     "Kurzschluss" and "prove" does not fire on "improve".
    /// </summary>
    /// <remarks>
    ///     A boundary is anything that is not a letter or digit, which is why a phrase may itself contain punctuation
    ///     (<c>tl;dr</c>) or a space.
    /// </remarks>
    private static bool ContainsPhrase(string upperText, string[] phrasesUpper)
    {
        foreach (var phrase in phrasesUpper)
        {
            var index = 0;
            while ((index = upperText.IndexOf(phrase, index, StringComparison.Ordinal)) >= 0)
            {
                var endIndex = index + phrase.Length;
                if (IsBoundaryBefore(upperText, index) && IsBoundaryAfter(upperText, endIndex))
                {
                    return true;
                }

                index = endIndex;
            }
        }

        return false;
    }

    // The neighbouring CHARACTER, not the UTF-16 code unit: char.IsLetterOrDigit rejects each half of a surrogate pair,
    // making every supplementary-plane letter a boundary. Rune decoding reads the scalar; a lone surrogate stays one.

    private static bool IsBoundaryBefore(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        _ = Rune.DecodeLastFromUtf16(text.AsSpan(start: 0, index), out var rune, out _);
        return !Rune.IsLetterOrDigit(rune);
    }

    private static bool IsBoundaryAfter(string text, int endIndex)
    {
        if (endIndex == text.Length)
        {
            return true;
        }

        _ = Rune.DecodeFromUtf16(text.AsSpan(endIndex), out var rune, out _);
        return !Rune.IsLetterOrDigit(rune);
    }

    private static string[] ToUpper(string[] phrases)
    {
        return [.. phrases.Select(static phrase => phrase.ToUpperInvariant())];
    }
}
