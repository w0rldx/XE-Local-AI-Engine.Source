namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Value object carrying invocation tool call state data.
/// </summary>
public sealed record InvocationToolCallState(
    string RequestId,
    string ToolName,
    string Parameters,
    DateTimeOffset RequestedAt);
