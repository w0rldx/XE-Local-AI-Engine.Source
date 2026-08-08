namespace XE_Local_AI_Engine.Client.Models.Events;

public sealed record ConversationPurgedEvent
{
    public required Guid ConversationId { get; init; }
}
