namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Request DTO for refresh worker token operations.
/// </summary>
public sealed record RefreshWorkerTokenRequest
{
    public required Guid ClientNodeId { get; init; }

    public required string RefreshToken { get; init; }
}
