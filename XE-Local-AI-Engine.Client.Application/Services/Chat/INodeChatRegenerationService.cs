namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Regenerates an assistant turn as a SIBLING VARIANT, driving the run through the same shared runner and pump as
///     a normal local turn: one call mints the linked variant placeholder, then drives and streams it.
/// </summary>
/// <remarks>
///     The endpoint/dispatcher stays thin: this service owns only the orchestration (build context up to the parent
///     user turn, CreatePlain, run, pump-persist INTO the variant row). It never overwrites the original — the
///     regenerate is always a new sibling in the same variant_group. Origin=Remote conversations are view-only and
///     rejected by the shared mutation guard before any persistence.
/// </remarks>
public interface INodeChatRegenerationService
{
    /// <summary>
    ///     Mints a sibling variant of <paramref name="originalMessageId" /> and drives it to completion, streaming the
    ///     run as <see cref="ChatStreamEvent" />s. Throws <see cref="NodeChatReadOnlyConversationException" /> for an
    ///     Origin=Remote conversation, <see cref="NodeChatConversationNotFoundException" /> for an unknown
    ///     conversation and <see cref="NodeChatMessageNotFoundException" /> for an unknown original message.
    /// </summary>
    /// <param name="samplingOptions">
    ///     Developer-gated per-turn sampling overrides, the same the send path carries; null leaves the package
    ///     byte-identical.
    /// </param>
    IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        bool useKnowledgeBase = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        SamplingOptions? samplingOptions = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Thrown when a chat operation names a conversation that does not exist. It is caller-triggerable, not an
///     internal invariant, so <c>LocalChatHub</c> turns it into a <c>HubException</c> the browser can show.
/// </summary>
public sealed class NodeChatConversationNotFoundException : InvalidOperationException
{
    public NodeChatConversationNotFoundException(Guid conversationId) : base($"Conversation {conversationId} was not found. It may have been deleted — reload the chat list.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}

/// <summary>
///     Thrown when a regenerate names an assistant message the conversation no longer holds. Same caller-triggerable
///     class as <see cref="NodeChatConversationNotFoundException" /> and translated by <c>LocalChatHub</c> the same way.
/// </summary>
public sealed class NodeChatMessageNotFoundException : InvalidOperationException
{
    public NodeChatMessageNotFoundException(Guid messageId) : base($"Message {messageId} was not found in this conversation. Reload the conversation and try again.")
    {
        MessageId = messageId;
    }

    public Guid MessageId { get; }
}
