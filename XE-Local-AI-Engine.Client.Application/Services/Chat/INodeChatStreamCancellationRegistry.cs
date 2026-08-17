namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Abstraction for node chat stream cancellation registry behavior.
/// </summary>
public interface INodeChatStreamCancellationRegistry
{
    /// <summary>
    ///     Claims <paramref name="correlation" /> for the calling stream. Throws
    ///     <see cref="NodeChatStreamAlreadyActiveException" /> when a stream is already registered under it.
    /// </summary>
    IDisposable Register(NodeChatMessageCorrelation correlation, Action cancel);

    bool TryCancel(NodeChatMessageCorrelation correlation);
}

/// <summary>
///     Thrown when a second stream tries to claim a correlation another in-flight stream already holds — a realistic
///     client double-invoke, not solely an internal invariant, so <c>LocalChatHub</c> translates it into a
///     <c>HubException</c> whose sentence the browser can show.
/// </summary>
public sealed class NodeChatStreamAlreadyActiveException()
    : InvalidOperationException("This message is already being generated. Wait for it to finish or stop it first.");
