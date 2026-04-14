namespace XE_Local_AI_Engine.Client.Models;

public sealed record InvocationCompletedPayload
{
    public required Guid InvocationId { get; init; }

    public required string FinalContent { get; init; }

    public string? ModelUsed { get; init; }

    public int? TokensUsed { get; init; }
}