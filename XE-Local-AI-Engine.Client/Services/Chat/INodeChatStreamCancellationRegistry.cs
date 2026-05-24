namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface INodeChatStreamCancellationRegistry
{
    IDisposable Register(NodeChatMessageCorrelation correlation, Action cancel);

    bool TryCancel(NodeChatMessageCorrelation correlation);
}
