namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying tool call request payload data.
/// </summary>
public sealed record ToolCallRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string ToolName { get; init; }

    public required string Parameters { get; init; }
}
