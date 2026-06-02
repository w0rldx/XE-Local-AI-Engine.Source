namespace XE_Local_AI_Engine.Client.Models;

public sealed record RefreshWorkerTokenRequest
{
    public required Guid ClientNodeId { get; init; }

    public required string RefreshToken { get; init; }
}
