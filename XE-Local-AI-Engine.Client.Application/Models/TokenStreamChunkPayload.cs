namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying token stream chunk payload data.
/// </summary>
public sealed record TokenStreamChunkPayload
{
    public required Guid InvocationId { get; init; }

    public required string Token { get; init; }

    public required bool IsComplete { get; init; }

    public long? SourceSequence { get; init; }
}
