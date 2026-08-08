namespace XE_Local_AI_Engine.Client.Models;

public sealed record WorkerHelloPayload
{
    public required Guid ClientNodeId { get; init; }
}
