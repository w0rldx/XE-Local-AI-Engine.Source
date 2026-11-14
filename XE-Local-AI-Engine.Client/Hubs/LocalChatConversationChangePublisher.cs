namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Pushes <see cref="LocalChatHubEvents.ConversationsChanged" /> on <see cref="LocalChatHub" /> after a conversation
///     metadata endpoint succeeded or a turn ended, so every open client's conversation list refreshes without a reload.
/// </summary>
/// <remarks>
///     Called by the REST endpoints and, once per terminal assistant message of a chat, by the persistence facade; never per
///     write, so an import or a streamed turn does not fan out. Best-effort: a transport failure is logged by type.
/// </remarks>
public sealed class LocalChatConversationChangePublisher : INodeChatConversationChangeNotifier
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

    /// <summary>Pushes <see cref="LocalChatHubEvents.ImportProgress" />: counts and a state only, never a title or text.</summary>
    /// <remarks>
    ///     Broadcast, not <c>Clients.User</c>: SignalR's default user id reads <c>NameIdentifier</c>, which these tokens
    ///     (<c>sub</c>, inbound claim mapping off) do not carry, so it would reach nobody. The client filters by its import id.
    /// </remarks>
    public async Task PublishImportProgressAsync(Guid importId, int importedChats, int totalChats, string state, CancellationToken cancellationToken)
    {
        var payload = new ImportProgressHubEvent
        {
            EventType = LocalChatHubEvents.ImportProgress,
            ImportId = importId,
            ImportedChats = importedChats,
            TotalChats = totalChats,
            State = state,
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        try
        {
            await _hubContext.Clients.All.SendAsync(LocalChatHubEvents.ImportProgress, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // Progress is advisory; the import's own response is the outcome.
            _logger.LogWarning("Could not publish chat import progress for {ImportId} ({ErrorClass}).", importId, exception.GetType().Name);
        }
    }
}
