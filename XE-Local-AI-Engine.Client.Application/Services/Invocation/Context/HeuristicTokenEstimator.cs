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
            TextContent text => text.Text?.Length ?? 0,
            TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
            FunctionCallContent call => EstimateCallCharacters(call),
            FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
            _ => content.ToString()?.Length ?? 0
        };
    }

    private static int EstimateCallCharacters(FunctionCallContent call)
    {
        var characters = call.Name.Length;
        if (call.Arguments is { } arguments)
        {
            foreach (var argument in arguments)
            {
                characters += argument.Key.Length + (argument.Value?.ToString()?.Length ?? 0);
            }
        }

        return characters;
    }
}
