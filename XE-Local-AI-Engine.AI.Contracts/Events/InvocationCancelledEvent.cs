namespace XE_Local_AI_Engine.AI.Contracts.Events;

public sealed record InvocationCancelledEvent(Guid InvocationId, string Reason);
