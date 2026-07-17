namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     Conservative, allocation-light token estimator for the provider-boundary budget middleware: ~1 token per
///     <see cref="CharsPerToken" /> weighted characters plus a small fixed per-message framing overhead, never calling
///     the provider. Non-ASCII characters are weighted <see cref="NonAsciiCharWeight" />× because byte-pair tokenizers
///     emit far more tokens per character for CJK / structured / emoji content than the chars/4 English heuristic
///     assumes — the plain divisor badly UNDER-counts there, which would let an over-window round through.
///     <para>
///         This is the AI.Agent-layer twin of <c>HeuristicTokenEstimator</c> in the application layer (which the outer
///         budgeter uses). The two live in separate assemblies by the layer arrow (Application → AI.Agent), so the
///         formula is intentionally duplicated: a change to the weighting here MUST be mirrored there, and vice versa.
///     </para>
///     <para>
///         AUD4-16: per-message estimates are memoized by message instance in a
///         <see cref="ConditionalWeakTable{TKey,TValue}" /> (no leak — the entry dies with the message). This hop
///         re-estimates the full message list on EVERY inner tool-loop round, and those rounds reuse the same
///         <see cref="ChatMessage" /> instances (the function-invocation loop appends but never mutates prior messages),
///         so the memo collapses repeated full-content scans to dictionary lookups. Correct only because a
///         <see cref="ChatMessage" /> is immutable-after-construction on these paths; the memoized value equals a fresh
///         computation, so budgeting output is unchanged.
///     </para>
/// </summary>
internal static class ProviderMessageTokenEstimator
{
    private static readonly ConditionalWeakTable<ChatMessage, StrongBox<int>> PerMessageEstimateCache = new();

    private const int CharsPerToken = 4;
    private const int PerMessageOverheadTokens = 4;

    // A non-ASCII char in a Latin-script language (German/French accents, ß, ç, …) counts as this many weighted chars:
    // modestly more than the chars/4 English rate, a small upward bias without over-counting European prose.
    private const int NonAsciiCharWeight = 2;

    // A CJK ideograph, kana, Hangul syllable, or emoji code unit counts as this many weighted chars. Byte-pair tokenizers
    // emit roughly one-or-more tokens PER CHARACTER for this content, whereas the chars/4 divisor assumes ~0.25 token/char
    // — a ~4x under-count that would let an over-window request through. Weighting these at CharsPerToken makes the
    // estimate ≈ 1 token/char (conservative). European accents deliberately keep the lighter NonAsciiCharWeight. Mirrored
    // in the application-layer HeuristicTokenEstimator (separate assembly by the layer arrow) — change both together.
    private const int CjkCharWeight = CharsPerToken;

    public static int EstimateTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return PerMessageEstimateCache.GetValue(message, ComputeMessageTokens).Value;
    }

    private static StrongBox<int> ComputeMessageTokens(ChatMessage message)
    {
        var weighted = 0;
        foreach (var content in message.Contents)
        {
            weighted += EstimateContentWeightedChars(content);
        }

        return new StrongBox<int>((weighted / CharsPerToken) + PerMessageOverheadTokens);
    }

    public static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            total += EstimateTokens(messages[index]);
        }

        return total;
    }

    /// <summary>Weighted-character count of a free-text span, treated as a token estimate for instructions / system prompt.</summary>
    public static int EstimateTokens(string? text)
    {
        return string.IsNullOrEmpty(text) ? 0 : (WeightedLength(text) / CharsPerToken) + PerMessageOverheadTokens;
    }

    /// <summary>
    ///     Conservative token estimate for the tool definitions serialized into the request: each tool's name,
    ///     description and JSON schema all count against the input window, so a tool-heavy agent must reserve room for
    ///     them. Ignored entirely, they under-count the round and let an over-window request through. Uses the same
    ///     weighted-char divisor and per-item framing overhead as message content.
    /// </summary>
    public static int EstimateTools(IEnumerable<AITool>? tools)
    {
        if (tools is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            var weighted = WeightedLength(tool.Name) + WeightedLength(tool.Description);
            if (tool is AIFunction function && function.JsonSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                weighted += WeightedLength(function.JsonSchema.GetRawText());
            }

            total += (weighted / CharsPerToken) + PerMessageOverheadTokens;
        }

        return total;
    }

    private static int EstimateContentWeightedChars(AIContent content)
    {
        return content switch
        {
            TextContent text => WeightedLength(text.Text),
            TextReasoningContent reasoning => WeightedLength(reasoning.Text),
            FunctionCallContent call => EstimateCallWeightedChars(call),
            FunctionResultContent result => WeightedLength(result.Result?.ToString()),
            _ => WeightedLength(content.ToString())
        };
    }

    private static int EstimateCallWeightedChars(FunctionCallContent call)
    {
        var weighted = WeightedLength(call.Name);
        if (call.Arguments is { } arguments)
        {
            foreach (var argument in arguments)
            {
                weighted += WeightedLength(argument.Key) + WeightedLength(argument.Value?.ToString());
            }
        }

        return weighted;
    }

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
    // excluded (they fall to the lighter NonAsciiCharWeight). Mirror of HeuristicTokenEstimator.IsCjkOrEmoji.
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
