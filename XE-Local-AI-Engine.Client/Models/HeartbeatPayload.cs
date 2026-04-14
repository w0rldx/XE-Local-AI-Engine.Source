namespace XE_Local_AI_Engine.Client.Models;

public sealed record HeartbeatPayload
{
    public required Guid ClientNodeId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}