namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Stable SignalR client-method name for knowledge-base indexing events. Doubles as the wire event-type discriminator
///     on the payload so the React client subscribes by name (mirrors <see cref="SchedulerHubEvents" />).
/// </summary>
public static class KnowledgeBaseHubEvents
{
    public const string DocumentChanged = "knowledge.documentChanged";
}

/// <summary>
///     Sanitized indexing-status payload: only the document id, its coarse pipeline status, and the instant the
///     transition was observed.
/// </summary>
/// <remarks>
///     Deliberately no file name, chunk text, or failure detail — the list refetch is the source of truth for the
///     display name and the reason.
/// </remarks>
public sealed class KnowledgeDocumentChangedHubEvent
{
    public required string EventType { get; init; }

    public required Guid DocumentId { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public required long OccurredAtUtc { get; init; }
}
