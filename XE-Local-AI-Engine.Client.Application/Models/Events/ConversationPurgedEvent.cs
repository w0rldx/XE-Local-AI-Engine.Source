namespace XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Value object carrying conversation purged event data.
/// </summary>
public sealed record ConversationPurgedEvent
{
    public required Guid ConversationId { get; init; }
}
