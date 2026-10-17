namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a send or a regenerate targets a conversation whose turn is parked on an approval or a question, or has
///     been answered and is waiting to resume: the user answers the waiting card first.
/// </summary>
/// <remarks>
///     A parked turn gives the invocation slot back, so a second turn here would run beside it and build its history from a
///     half-finished turn. <c>LocalChatHub</c> prefixes it with the <c>ConversationTurnParked</c> conflict type; on REST the
///     <c>ConflictExceptionHandler</c> maps it to a 409 with the same name.
/// </remarks>
public sealed class NodeChatTurnParkedException : InvalidOperationException
{
    public NodeChatTurnParkedException(Guid conversationId) : base($"Conversation {conversationId} has a reply waiting on an approval or a question; answer the waiting card before sending another message.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}
