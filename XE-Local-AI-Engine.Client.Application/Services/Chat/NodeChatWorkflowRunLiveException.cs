namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a normal send targets a conversation whose bound graph-workflow run is still live, parked runs
///     included: the user answers through the workflow path, or stops the run first.
/// </summary>
/// <remarks>
///     <c>LocalChatHub</c> prefixes it with the <c>GraphWorkflowRunLiveInConversation</c> conflict type; on REST the
///     <c>ConflictExceptionHandler</c> maps it to a 409 with the same name.
/// </remarks>
public sealed class NodeChatWorkflowRunLiveException : InvalidOperationException
{
    public NodeChatWorkflowRunLiveException(Guid conversationId) : base($"Conversation {conversationId} has a workflow run in progress; answer or stop the workflow before sending a normal message.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}
