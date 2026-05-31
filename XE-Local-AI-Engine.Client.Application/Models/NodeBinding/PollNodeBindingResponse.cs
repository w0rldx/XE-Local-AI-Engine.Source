namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Response DTO for poll node binding operations.
/// </summary>
public sealed record PollNodeBindingResponse
{
    public required string Status { get; init; }

    public int IntervalSeconds { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public PairClientResponse? Credentials { get; init; }
}
