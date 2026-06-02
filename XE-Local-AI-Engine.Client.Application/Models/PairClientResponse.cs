namespace XE_Local_AI_Engine.Client.Models;

public sealed record PairClientResponse
{
    public required Guid ClientNodeId { get; init; }

    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
