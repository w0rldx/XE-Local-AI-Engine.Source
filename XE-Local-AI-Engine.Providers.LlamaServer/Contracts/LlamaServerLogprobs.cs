namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using Microsoft.Extensions.AI;
using OpenAI.Chat;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>Asks llama-server for per-entry probabilities on a chat completion and reads them off a streamed chunk.</summary>
/// <remarks>
///     MEAI's adapter drops <see cref="ChatOptions.AdditionalProperties" />, so the request rides the typed body. An entry
///     is not a token: partial UTF-8 tokens merge into one entry whose values belong to the last token, and a stream can
///     omit entries (the final one always, others with thinking on). Measured on the pinned b12014; tools plus stream is
///     an HTTP 400, so this is for a request without tools. Evidence: docs/wiki/03-local-runtime-and-providers.md.
/// </remarks>
public static class LlamaServerLogprobs
{
    /// <summary>Returns a clone of <paramref name="options" /> that requests <paramref name="topLogprobs" /> alternatives per entry; the caller validates the count, since upstream has no limit.</summary>
    public static ChatOptions Request(ChatOptions options, int topLogprobs) =>
        OpenAICompatibleRequestBody.Chain(options,
            body =>
            {
                body.IncludeLogProbabilities = true;
                body.TopLogProbabilityCount = topLogprobs;
            });

    /// <summary>The entries one streamed update carries; empty for a chunk without any or another provider's update.</summary>
    public static IReadOnlyList<ChatTokenLogProbabilityDetails> Read(object? rawRepresentation) =>
        rawRepresentation is StreamingChatCompletionUpdate update ? update.ContentTokenLogProbabilities : [];
}
