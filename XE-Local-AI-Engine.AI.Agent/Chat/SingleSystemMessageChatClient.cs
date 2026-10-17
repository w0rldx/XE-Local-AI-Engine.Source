namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;

/// <summary>
///     Hands the provider ONE leading system message: <see cref="ChatOptions.Instructions" /> and the leading run of
///     system messages are folded into a single message at the start.
/// </summary>
/// <remarks>
///     The OpenAI-shaped adapter sends <see cref="ChatOptions.Instructions" /> (the skill listing) as a system message of
///     its own ahead of the seed system prompt, and templates such as Qwen3.5's raise "System message must be at the
///     beginning" on the second one. The fold keeps that order; later system messages stay where they are.
/// </remarks>
internal sealed class SingleSystemMessageChatClient : DelegatingChatClient
{
    public SingleSystemMessageChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var folded = Fold(messages, options);
        return base.GetResponseAsync(folded.Messages, folded.Options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var folded = Fold(messages, options);
        return base.GetStreamingResponseAsync(folded.Messages, folded.Options, cancellationToken);
    }

    /// <summary>The messages and options the provider receives; the inputs unchanged when they already carry at most one system block.</summary>
    internal static FoldedRequest Fold(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var leadingSystemCount = 0;
        while (leadingSystemCount < materialized.Count && materialized[leadingSystemCount].Role == ChatRole.System)
        {
            leadingSystemCount++;
        }

        var instructions = options?.Instructions;
        var hasInstructions = !string.IsNullOrEmpty(instructions);
        if (leadingSystemCount + (hasInstructions ? 1 : 0) < 2 || !AreFoldable(materialized, leadingSystemCount))
        {
            return new FoldedRequest
            {
                Messages = materialized,
                Options = options
            };
        }

        var parts = new List<string>(leadingSystemCount + 1);
        if (hasInstructions)
        {
            parts.Add(instructions!);
        }

        for (var index = 0; index < leadingSystemCount; index++)
        {
            if (!string.IsNullOrEmpty(materialized[index].Text))
            {
                parts.Add(materialized[index].Text);
            }
        }

        var folded = new List<ChatMessage>(materialized.Count - leadingSystemCount + 1)
        {
            new(ChatRole.System, string.Join("\n\n", parts))
        };
        for (var index = leadingSystemCount; index < materialized.Count; index++)
        {
            folded.Add(materialized[index]);
        }

        var foldedOptions = options;
        if (hasInstructions)
        {
            foldedOptions = options!.Clone();
            foldedOptions.Instructions = null;
        }

        return new FoldedRequest
        {
            Messages = folded,
            Options = foldedOptions
        };
    }

    // The fold keeps only text, so a system message carrying anything else (non-text content, provider properties) is
    // sent as it is rather than silently losing that part.
    private static bool AreFoldable(IReadOnlyList<ChatMessage> messages, int leadingSystemCount)
    {
        for (var index = 0; index < leadingSystemCount; index++)
        {
            var message = messages[index];
            if (message.AdditionalProperties is { Count: > 0 } || message.Contents.Any(static content => content is not TextContent))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The messages and options handed to the provider.</summary>
    internal sealed class FoldedRequest
    {
        public required IReadOnlyList<ChatMessage> Messages { get; init; }

        public required ChatOptions? Options { get; init; }
    }
}
