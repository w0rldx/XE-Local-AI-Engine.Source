namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;

/// <summary>
///     Conservative character-count token estimator: ~1 token per <see cref="CharsPerToken" /> characters plus a small
///     fixed per-message framing overhead. It never calls the provider, so it is deterministic and allocation-light on
///     the streaming hot path. It intentionally over- rather than under-estimates (the framing overhead and the coarse
///     divisor bias upward) so the budgeter trims early rather than overrunning the launched context window. A
///     provider-accurate implementation of <see cref="ITokenEstimator" /> can replace this later without touching the
///     budgeting policy.
/// </summary>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    // GPT/LLaMA-family byte-pair tokenizers average roughly four characters per token for English prose; using a
    // divisor of four (rather than a larger one) keeps the estimate conservative for code and non-English text where
    // tokens are shorter.
    private const int CharsPerToken = 4;

    // Every message carries role/delimiter framing the character count alone misses; a small fixed floor keeps a
    // near-empty message (e.g. a bare tool acknowledgement) from being counted as zero-cost.
    private const int PerMessageOverheadTokens = 4;

    // A non-ASCII character counts as this many weighted characters. Byte-pair tokenizers emit far more tokens per
    // character for CJK / structured / emoji content than the chars/4 English heuristic assumes, so the plain divisor
    // badly UNDER-counts there; weighting non-ASCII up biases the estimate upward (conservative), keeping the budgeter
    // trimming early rather than overrunning the window on non-Latin conversations. Mirrored in the AI.Agent-layer
    // ProviderMessageTokenEstimator (separate assembly by the layer arrow) — change both together.
    private const int NonAsciiCharWeight = 2;

    public int EstimateTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var characters = 0;
        foreach (var content in message.Contents)
        {
            characters += EstimateContentCharacters(content);
        }

        return (characters / CharsPerToken) + PerMessageOverheadTokens;
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

    // Character count with each non-ASCII code unit weighted NonAsciiCharWeight× (see the field comment).
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
