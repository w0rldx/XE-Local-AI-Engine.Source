namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Stable SignalR client-method names the local chat hub pushes. Each doubles as the payload's event-type
///     discriminator, so the React client subscribes by name (mirrors <see cref="KnowledgeBaseHubEvents" />).
/// </summary>
public static class LocalChatHubEvents
{
    public const string ConversationsChanged = "chat.conversationsChanged";
}

/// <summary>
///     A conversation's list metadata changed through the API (rename, pin, archive, delete, import), so an open
///     conversation list refetches. Ids only: the refetch is the source of truth for titles.
/// </summary>
public sealed class ConversationsChangedHubEvent
{
    public required string EventType { get; init; }

    /// <summary>The changed conversation; <see langword="null" /> when several changed at once (an import).</summary>
    public Guid? ConversationId { get; init; }

    public required long OccurredAtUtc { get; init; }
}
