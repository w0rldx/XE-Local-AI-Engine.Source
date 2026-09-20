namespace XE_Local_AI_Engine.AI.Contracts.Events;

public sealed class ApprovalResolvedEvent
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }
}
