namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;

/// <summary>
///     Pushes <see cref="LocalChatHubEvents.ConversationsChanged" /> on <see cref="LocalChatHub" /> after a conversation
///     metadata endpoint succeeded, so every open client's conversation list refreshes without a reload.
/// </summary>
/// <remarks>
///     Called by the REST endpoints rather than the persistence facade, so an import's or an integration's internal writes
///     never fan out one push per conversation. Best-effort: a transport failure is logged by type and never fails the request.
/// </remarks>
public sealed class LocalChatConversationChangePublisher
{
    private readonly IHubContext<LocalChatHub> _hubContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalChatConversationChangePublisher> _logger;

    public LocalChatConversationChangePublisher(IHubContext<LocalChatHub> hubContext,
        TimeProvider timeProvider,
        ILogger<LocalChatConversationChangePublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _hubContext = hubContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <param name="conversationId">The changed conversation, or <see langword="null" /> when several changed at once.</param>
    public async Task PublishAsync(Guid? conversationId, CancellationToken cancellationToken)
    {
        var payload = new ConversationsChangedHubEvent
        {
            EventType = LocalChatHubEvents.ConversationsChanged,
            ConversationId = conversationId,
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        try
        {
            await _hubContext.Clients.All.SendAsync(LocalChatHubEvents.ConversationsChanged, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // The change is committed; a lost push only delays other clients until their next refetch.
            _logger.LogWarning("Could not publish a conversation change for {ConversationId} ({ErrorClass}).", conversationId, exception.GetType().Name);
        }
    }
}
