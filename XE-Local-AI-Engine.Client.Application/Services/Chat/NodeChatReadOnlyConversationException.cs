namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a mutation targets a read-only conversation: an <c>Origin=Remote</c> mirror or a scheduled run's answer.
/// </summary>
/// <remarks>
///     On the REST path the global <c>ConflictExceptionHandler</c> turns it into a 409 with
///     <c>conflictType = ReadOnlyConversation</c>, so endpoints must let it propagate and never catch it; the local
///     send and stream path propagates it to the caller.
/// </remarks>
public sealed class NodeChatReadOnlyConversationException : InvalidOperationException
{
    public NodeChatReadOnlyConversationException(Guid conversationId) : this(conversationId, "it has remote origin")
    {
    }

    /// <param name="conversationId">The refused conversation.</param>
    /// <param name="reason">Why it is read-only, completing "is read-only because …".</param>
    public NodeChatReadOnlyConversationException(Guid conversationId, string reason) : base($"Conversation {conversationId} is read-only because {reason}.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}
