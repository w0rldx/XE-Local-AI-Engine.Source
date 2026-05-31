namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Request DTO for pair client operations.
/// </summary>
public sealed record PairClientRequest
{
    public required string Token { get; init; }

    public required string NodeName { get; init; }
}
