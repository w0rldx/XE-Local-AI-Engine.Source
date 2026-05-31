namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Application service for i node chat stream behavior.
/// </summary>
public interface INodeChatStreamService
{
    IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
        CancellationToken cancellationToken = default);
}
