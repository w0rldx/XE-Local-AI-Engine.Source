namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Application-side seam the chat persistence facade calls once a turn's assistant message is terminal, so every
///     open client's conversation list refreshes its order and preview.
/// </summary>
/// <remarks>
///     The Client host supplies the hub-backed implementation; with none registered the facade pushes nothing.
///     Best-effort: an implementation never throws, and the payload carries ids only.
/// </remarks>
public interface INodeChatConversationChangeNotifier
{
    /// <param name="conversationId">The changed conversation, or <see langword="null" /> when several changed at once.</param>
    Task PublishAsync(Guid? conversationId, CancellationToken cancellationToken);
}
