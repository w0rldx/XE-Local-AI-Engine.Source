namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Abstraction for node chat stream cancellation registry behavior.
/// </summary>
public interface INodeChatStreamCancellationRegistry
{
    IDisposable Register(NodeChatMessageCorrelation correlation, Action cancel);

    bool TryCancel(NodeChatMessageCorrelation correlation);
}
