namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatCompletion = OpenAI.Chat.ChatCompletion;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;

/// <summary>
///     Recovers the reasoning channel of an OpenAI-compatible server whose shape the MEAI adapter does not already
///     understand — and does NOTHING when it does.
/// </summary>
/// <remarks>
///     <para>
///         Three shapes exist in the wild. <c>reasoning_content</c> (llama.cpp / DeepSeek) is already mapped to
///         <see cref="TextReasoningContent" /> by the pinned Microsoft.Extensions.AI.OpenAI 10.9.0 adapter, streaming
///         and non-streaming alike — verified against the real adapter — so this client passes it straight through and
///         must never re-derive it, or the same thinking would surface twice.
///     </para>
///     <para>
///         The two it does handle: (a) newer vLLM builds send a bare <c>reasoning</c> field, which the adapter drops
///         because the typed OpenAI schema has no such property — it is recovered from the SDK model's JSON patch,
///         where the raw payload survives deserialization; and (b) servers that inline the thinking as leading
///         <c>&lt;think&gt;…&lt;/think&gt;</c> text, handled by <see cref="ThinkTagReasoningSplitter" />.
///     </para>
///     <para>
///         Both fallbacks are gated on having seen NO reasoning content yet: once a response has produced reasoning
///         through any channel, its content is left completely alone. That keeps the common (already-correct) path
///         byte-identical and makes a double conversion impossible.
///     </para>
/// </remarks>
internal sealed class ExternalReasoningRewritingChatClient : DelegatingChatClient
{
    private static ReadOnlySpan<byte> NonStreamingReasoningPath => "$.choices[0].message.reasoning"u8;

    private static ReadOnlySpan<byte> StreamingReasoningPath => "$.choices[0].delta.reasoning"u8;

    public ExternalReasoningRewritingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (response.Messages.Any(static message => message.Contents.Any(static content => content is TextReasoningContent)))
        {
            return response;
        }

        if (TryReadReasoning(response.RawRepresentation as ChatCompletion) is { } reasoning)
        {
            foreach (var message in response.Messages)
            {
                message.Contents.Insert(index: 0, new TextReasoningContent(reasoning));
            }

            return response;
        }

        foreach (var message in response.Messages)
        {
            RewriteInlineThinking(message.Contents);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var splitter = new ThinkTagReasoningSplitter();

        // Two distinct flags, and the distinction is load-bearing. `sawAdapterReasoning` gates only the raw-payload
        // recovery: once the adapter has mapped a reasoning field itself, reading the raw one too would double it.
        // `sawAnyReasoning` gates the <think> fallback, which is the last resort for a server that surfaced no reasoning
        // channel at all. Collapsing them into one flag silently drops every reasoning delta after the first, because a
        // server streams `reasoning` in as many pieces as it streams content.
        var sawAdapterReasoning = false;
        var sawAnyReasoning = false;
        ChatResponseUpdate? lastUpdate = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            lastUpdate = update;
            if (update.Contents.Any(static content => content is TextReasoningContent))
            {
                sawAdapterReasoning = true;
                sawAnyReasoning = true;
                yield return update;
                continue;
            }

            if (!sawAdapterReasoning && TryReadReasoning(update.RawRepresentation as StreamingChatCompletionUpdate) is { } reasoning)
            {
                sawAnyReasoning = true;
                update.Contents.Insert(index: 0, new TextReasoningContent(reasoning));
                yield return update;
                continue;
            }

            if (!sawAnyReasoning && !splitter.IsPassthrough)
            {
                update.Contents = SplitInlineThinking(update.Contents, splitter);
            }

            yield return update;
        }

        // A stream that ended while the splitter still held buffered text still owes the caller that text; emit it on a
        // trailing update carrying the last real update's identity, so downstream aggregation attributes it correctly.
        if (lastUpdate is null || sawAnyReasoning || splitter.IsPassthrough)
        {
            yield break;
        }

        var flushed = splitter.Flush();
        if (flushed.Count == 0)
        {
            yield break;
        }

        yield return new ChatResponseUpdate(lastUpdate.Role, [.. flushed])
        {
            ResponseId = lastUpdate.ResponseId,
            MessageId = lastUpdate.MessageId,
            ConversationId = lastUpdate.ConversationId,
            ModelId = lastUpdate.ModelId,
            CreatedAt = lastUpdate.CreatedAt
        };
    }

    /// <summary>
    ///     Rewrites a completed message's contents ONLY when a leading think block was actually found. Leaving the
    ///     original content items in place otherwise matters: rebuilding them would drop anything the adapter attached
    ///     to a <see cref="TextContent" /> for a response that never needed rewriting at all.
    /// </summary>
    private static void RewriteInlineThinking(IList<AIContent> contents)
    {
        var splitter = new ThinkTagReasoningSplitter();
        var rewritten = SplitInlineThinking(contents, splitter);
        foreach (var trailing in splitter.Flush())
        {
            rewritten.Add(trailing);
        }

        if (!rewritten.Any(static content => content is TextReasoningContent))
        {
            return;
        }

        contents.Clear();
        foreach (var content in rewritten)
        {
            contents.Add(content);
        }
    }

    // Feeds every text item through the splitter, preserving non-text items (tool calls, usage, …) and their ordering.
    private static IList<AIContent> SplitInlineThinking(IList<AIContent> contents, ThinkTagReasoningSplitter splitter)
    {
        var rewritten = new List<AIContent>(contents.Count);
        foreach (var content in contents)
        {
            if (content is not TextContent text)
            {
                rewritten.Add(content);
                continue;
            }

            rewritten.AddRange(splitter.Push(text.Text));
        }

        return rewritten;
    }

    private static string? TryReadReasoning(ChatCompletion? completion)
    {
        if (completion is null)
        {
            return null;
        }

        // SCME0001: the SDK model's JsonPatch is [Experimental]. It is the only place a server's unmapped fields survive
        // deserialization, and reading it is strictly additive — the same scoped-suppression pattern the outbound
        // request-body patch uses. Suppression is scoped to the single call.
#pragma warning disable SCME0001
        return completion.Patch.TryGetJson(NonStreamingReasoningPath, out var raw) ? ReadJsonString(raw) : null;
#pragma warning restore SCME0001
    }

    private static string? TryReadReasoning(StreamingChatCompletionUpdate? update)
    {
        if (update is null)
        {
            return null;
        }

#pragma warning disable SCME0001 // See TryReadReasoning(ChatCompletion).
        return update.Patch.TryGetJson(StreamingReasoningPath, out var raw) ? ReadJsonString(raw) : null;
#pragma warning restore SCME0001
    }

    /// <summary>
    ///     Reads a JSON string value, or <see langword="null" /> for any other shape or malformed input — a best-effort
    ///     enrichment must never fail a turn over a server that put something unexpected in the field.
    /// </summary>
    private static string? ReadJsonString(ReadOnlyMemory<byte> raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
