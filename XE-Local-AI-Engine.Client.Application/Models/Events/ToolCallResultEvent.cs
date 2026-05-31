namespace XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Value object carrying tool call result event data.
/// </summary>
public sealed record ToolCallResultEvent
{
    public required string RequestId { get; init; }

    public required string Result { get; init; }

    public string? Error { get; init; }
}
