namespace XE_Local_AI_Engine.Models;

public sealed record PairClientRequest
{
    public required string Token { get; init; }

    public required string NodeName { get; init; }
}
