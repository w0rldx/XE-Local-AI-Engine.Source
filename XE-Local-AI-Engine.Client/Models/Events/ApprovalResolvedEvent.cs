namespace XE_Local_AI_Engine.Client.Models.Events;

public sealed record ApprovalResolvedEvent
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }
}
