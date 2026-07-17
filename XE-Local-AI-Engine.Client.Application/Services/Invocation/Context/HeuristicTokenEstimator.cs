namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     Conservative character-count token estimator: ~1 token per <see cref="CharsPerToken" /> characters plus a small
///     fixed per-message framing overhead. It never calls the provider, so it is deterministic and allocation-light on
///     the streaming hot path. It intentionally over- rather than under-estimates (the framing overhead and the coarse
///     divisor bias upward) so the budgeter trims early rather than overrunning the launched context window. A
///     provider-accurate implementation of <see cref="ITokenEstimator" /> can replace this later without touching the
///     budgeting policy.
/// </summary>
/// <remarks>
///     AUD4-16: per-message estimates are memoized by message instance in a <see cref="ConditionalWeakTable{TKey,TValue}" />
///     (no leak — the entry dies with the message). The budgeter re-estimates the same history across the two outer
///     growth points and every inner tool-loop round, and the same <see cref="ChatMessage" /> instances flow through all
///     of them (the runner appends but never mutates), so the memo turns repeated full-content scans into dictionary
///     lookups. Correct only because a <see cref="ChatMessage" /> is immutable-after-construction on these paths
///     (truncation produces a NEW instance); the returned value is identical to a fresh computation, so budgeting output
///     is unchanged.
/// </remarks>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    private static readonly ConditionalWeakTable<ChatMessage, StrongBox<int>> PerMessageEstimateCache = new();

    // GPT/LLaMA-family byte-pair tokenizers average roughly four characters per token for English prose; using a
    // divisor of four (rather than a larger one) keeps the estimate conservative for code and non-English text where
    // tokens are shorter.
    private const int CharsPerToken = 4;

    // Every message carries role/delimiter framing the character count alone misses; a small fixed floor keeps a
    // near-empty message (e.g. a bare tool acknowledgement) from being counted as zero-cost.
    private const int PerMessageOverheadTokens = 4;

    // A non-ASCII character in a Latin-script language (German/French accents, ß, ç, …) counts as this many weighted
    // characters: those tokenize to modestly more than the chars/4 English rate, so a small upward bias keeps the estimate
    // conservative without over-counting European prose.
    private const int NonAsciiCharWeight = 2;

    // A CJK ideograph, kana, Hangul syllable, or emoji code unit counts as this many weighted characters. Byte-pair
    // tokenizers emit roughly one-or-more tokens PER CHARACTER for this content (a Han character is commonly a whole token
    // or splits into two-to-three byte tokens), whereas the chars/4 divisor assumes ~0.25 token/char — a ~4x under-count.
    // Weighting these at CharsPerToken makes the estimate ≈ 1 token/char (conservative, upper-biased). European accents
    // deliberately keep the lighter NonAsciiCharWeight so German/French text is not over-counted. Mirrored in the
    // AI.Agent-layer ProviderMessageTokenEstimator (separate assembly by the layer arrow) — change both together.
    private const int CjkCharWeight = CharsPerToken;

    public int EstimateTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return PerMessageEstimateCache.GetValue(message, ComputeMessageTokens).Value;
    }

    private static StrongBox<int> ComputeMessageTokens(ChatMessage message)
    {
        var characters = 0;
        foreach (var content in message.Contents)
        {
            characters += EstimateContentCharacters(content);
        }

        return new StrongBox<int>((characters / CharsPerToken) + PerMessageOverheadTokens);
    }

    public int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            total += EstimateTokens(messages[index]);
        }

        return total;
    }

    private static int EstimateContentCharacters(AIContent content)
    {
        return content switch
        {
            TextContent text => WeightedLength(text.Text),
            TextReasoningContent reasoning => WeightedLength(reasoning.Text),
            FunctionCallContent call => EstimateCallCharacters(call),
            FunctionResultContent result => WeightedLength(result.Result?.ToString()),
            _ => WeightedLength(content.ToString())
        };
    }

    private static int EstimateCallCharacters(FunctionCallContent call)
    {
        var characters = WeightedLength(call.Name);
        if (call.Arguments is { } arguments)
        {
            foreach (var argument in arguments)
            {
                characters += WeightedLength(argument.Key) + WeightedLength(argument.Value?.ToString());
            }
        }

        return characters;
    }

    // Character count with each code unit weighted by script (see the field comments): ASCII 1, CJK/kana/Hangul/emoji
    // CjkCharWeight, other non-ASCII NonAsciiCharWeight.
    private static int WeightedLength(string? value)
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

    private static int WeightOf(char character)
    {
        if (character < 128)
        {
            return 1;
        }

        return IsCjkOrEmoji(character) ? CjkCharWeight : NonAsciiCharWeight;
    }

    // Code units that tokenize to ≈1+ tokens each: CJK radicals/ideographs (incl. Ext-A) + kana + CJK punctuation
    // (U+2E80–U+9FFF), Hangul syllables (U+AC00–U+D7A3), CJK compatibility ideographs (U+F900–U+FAFF), half/fullwidth
    // forms (U+FF00–U+FFEF), and surrogate halves (U+D800–U+DFFF) — which stand in for emoji and CJK Ext-B+ code points,
    // each surrogate counted heavy so a two-unit emoji biases upward. Latin-1/Latin-Extended accents are intentionally
    // excluded (they fall to the lighter NonAsciiCharWeight).
    // The bounds are written as \u escapes ONLY: a CJK literal and its compatibility clone (e.g. U+8C48 vs U+F900) are
    // visually identical, and exactly that ambiguity once silently diverged the two mirrored copies of this method.
    private static bool IsCjkOrEmoji(char character)
    {
        return character is (>= '\u2E80' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7A3')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\uFF00' and <= '\uFFEF')
            or (>= '\uD800' and <= '\uDFFF');
    }
}
