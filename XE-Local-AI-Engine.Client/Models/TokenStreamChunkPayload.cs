namespace XE_Local_AI_Engine.Client.Models;

public sealed record TokenStreamChunkPayload
{
    public required Guid InvocationId { get; init; }

    public required string Token { get; init; }

    public required bool IsComplete { get; init; }
}