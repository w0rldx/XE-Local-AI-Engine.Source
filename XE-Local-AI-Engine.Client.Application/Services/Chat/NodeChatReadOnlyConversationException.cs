namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a mutation targets a read-only (<c>Origin=Remote</c>) conversation.
/// </summary>
/// <remarks>
///     On the REST path the global <c>ConflictExceptionHandler</c> turns it into a 409 with
///     <c>conflictType = ReadOnlyConversation</c>, so endpoints must let it propagate and never catch it; the local
///     send and stream path propagates it to the caller.
/// </remarks>
public sealed class NodeChatReadOnlyConversationException : InvalidOperationException
{
    public NodeChatReadOnlyConversationException(Guid conversationId) : base($"Conversation {conversationId} is read-only because it has remote origin.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}
