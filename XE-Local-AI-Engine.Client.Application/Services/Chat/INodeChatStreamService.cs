namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface INodeChatStreamService
{
    IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
        CancellationToken cancellationToken = default);
}
