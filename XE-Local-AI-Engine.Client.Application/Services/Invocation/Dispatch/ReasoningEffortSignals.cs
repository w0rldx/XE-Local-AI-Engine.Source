namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;

/// <summary>
///     The deterministic ladder behind reasoning effort <c>auto</c>: the phrase vocabularies and the integer score
///     that turns one turn's SHAPE into a <see cref="ReasoningTier" />. Separated from the dispatcher so the whole
///     rule set is table-testable without a container, and so the two translated phrase lists live in one place.
///     <para>
///         <b>Determinism.</b> There are exactly two degrees of freedom and both are pinned: phrase folding is
///         <c>ToUpperInvariant</c> (the repo's CA1308 posture — never fold to lower case), and every comparison is
///         <see cref="StringComparison.Ordinal" />. No clock, no randomness, no culture.
///     </para>
///     <para>
///         <b>What is NOT here.</b> The offered tool count is deliberately not a score term. <c>ask_user</c> is
///         merged into every tool-capable offer and the relevance ranker pins an always-on core set, so
///         "tools are offered" holds on essentially every tools-mode turn: as a score term it cancelled the
///         short-turn signal and made <see cref="ReasoningTier.Fast" /> unreachable whenever tools were on. Tools
///         refuse the model SWAP instead — the tier is never demoted by anything.
///     </para>
/// </summary>
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
        var upper = text.ToUpperInvariant();

        var hasCodeFence = text.Contains(CodeFence, StringComparison.Ordinal);
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

        return isDeepContext ? ReasoningDispatchReasons.DeepContext : ReasoningDispatchReasons.Balanced;
    }

    /// <summary>
    ///     Word-boundary containment: the phrase must not be embedded inside a longer word, so "kurz" does not fire on
    ///     "Kurzschluss" and "prove" does not fire on "improve". A boundary is anything that is not a letter or digit —
    ///     which is why a phrase may itself contain punctuation (<c>tl;dr</c>) or a space.
    /// </summary>
    private static bool ContainsPhrase(string upperText, string[] phrasesUpper)
    {
        foreach (var phrase in phrasesUpper)
        {
            var index = 0;
            while ((index = upperText.IndexOf(phrase, index, StringComparison.Ordinal)) >= 0)
            {
                var startsAtBoundary = index == 0 || !char.IsLetterOrDigit(upperText[index - 1]);
                var endIndex = index + phrase.Length;
                var endsAtBoundary = endIndex == upperText.Length || !char.IsLetterOrDigit(upperText[endIndex]);
                if (startsAtBoundary && endsAtBoundary)
                {
                    return true;
                }

                index = endIndex;
            }
        }

        return false;
    }

    private static string[] ToUpper(string[] phrases)
    {
        return [.. phrases.Select(static phrase => phrase.ToUpperInvariant())];
    }
}
