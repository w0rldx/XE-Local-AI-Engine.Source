namespace XE_Local_AI_Engine.Client.Models.Events;

public sealed record InvocationCancelledEvent
{
    public required Guid InvocationId { get; init; }

    public required string Reason { get; init; }
}