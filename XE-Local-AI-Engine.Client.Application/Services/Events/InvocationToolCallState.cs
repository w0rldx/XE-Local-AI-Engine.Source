namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed class InvocationToolCallState
{
    public required string RequestId { get; init; }

    public required string ToolName { get; init; }

    public required string Parameters { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}
