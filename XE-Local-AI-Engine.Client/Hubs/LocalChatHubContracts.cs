namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Stable SignalR client-method names the local chat hub pushes. Each doubles as the payload's event-type
///     discriminator, so the React client subscribes by name (mirrors <see cref="KnowledgeBaseHubEvents" />).
/// </summary>
public static class LocalChatHubEvents
{
    public const string ConversationsChanged = "chat.conversationsChanged";

    public const string ImportProgress = "chat.importProgress";
}

/// <summary>
///     A conversation's list metadata changed (a rename, pin, archive, delete or import through the API, or a finished
///     turn), so an open conversation list refetches. Ids only: the refetch is the source of truth for titles.
/// </summary>
public sealed class ConversationsChangedHubEvent
{
    public required string EventType { get; init; }

    /// <summary>The changed conversation; <see langword="null" /> when several changed at once (an import).</summary>
    public Guid? ConversationId { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>Values of <see cref="ImportProgressHubEvent.State" />.</summary>
public static class ImportProgressStates
{
    public const string Running = "running";

    public const string Finished = "finished";

    /// <summary>A write failed after validation; the chats this import had written are purged again.</summary>
    public const string Failed = "failed";
}

/// <summary>
///     Progress of a chat import that named an import id, so the client that started it can show how far it got.
///     Counts only, never a title or message.
/// </summary>
public sealed class ImportProgressHubEvent
{
    public required string EventType { get; init; }

    /// <summary>The id the client passed as <c>importId</c>.</summary>
    public required Guid ImportId { get; init; }

    public required int ImportedChats { get; init; }

    public required int TotalChats { get; init; }

    /// <summary>One of <see cref="ImportProgressStates" />.</summary>
    public required string State { get; init; }

    public required long OccurredAtUtc { get; init; }
}
