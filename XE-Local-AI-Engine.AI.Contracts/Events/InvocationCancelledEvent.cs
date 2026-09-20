namespace XE_Local_AI_Engine.AI.Contracts.Events;

public sealed class InvocationCancelledEvent
{
    public required Guid InvocationId { get; init; }

    public required string Reason { get; init; }
}
