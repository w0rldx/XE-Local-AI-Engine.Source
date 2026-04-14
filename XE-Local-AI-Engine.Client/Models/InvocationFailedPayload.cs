namespace XE_Local_AI_Engine.Client.Models;

public sealed record InvocationFailedPayload
{
    public required Guid InvocationId { get; init; }

    public required string Error { get; init; }

    public string? FailureCategory { get; init; }
}