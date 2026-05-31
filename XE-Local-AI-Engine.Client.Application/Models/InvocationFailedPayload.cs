namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying invocation failed payload data.
/// </summary>
public sealed record InvocationFailedPayload
{
    public required Guid InvocationId { get; init; }

    public Guid? MessageId { get; init; }

    public required string Error { get; init; }

    public string? FailureCategory { get; init; }
}
