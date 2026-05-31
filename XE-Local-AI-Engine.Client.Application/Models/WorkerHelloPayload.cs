namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying worker hello payload data.
/// </summary>
public sealed record WorkerHelloPayload
{
    public required Guid ClientNodeId { get; init; }
}
