namespace XE_Local_AI_Engine.AI.Agent.Chat;

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
/// </summary>
internal static class ProviderMessageTokenEstimator
{
    private const int CharsPerToken = 4;
    private const int PerMessageOverheadTokens = 4;

    // A non-ASCII char counts as this many weighted chars. CJK/structured/emoji tokenize to roughly one-or-more tokens
    // per char, so weighting them up biases the estimate upward (conservative) rather than the chars/4 under-count.
    private const int NonAsciiCharWeight = 2;

    public static int EstimateTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var weighted = 0;
        foreach (var content in message.Contents)
        {
            weighted += EstimateContentWeightedChars(content);
        }

        return (weighted / CharsPerToken) + PerMessageOverheadTokens;
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
            weighted += character < 128 ? 1 : NonAsciiCharWeight;
        }

        return weighted;
    }
}
