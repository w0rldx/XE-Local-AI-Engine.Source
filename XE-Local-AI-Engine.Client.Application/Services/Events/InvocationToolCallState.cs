namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed record InvocationToolCallState(
    string RequestId,
    string ToolName,
    string Parameters,
    DateTimeOffset RequestedAt);
