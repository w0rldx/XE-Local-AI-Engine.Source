namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Value object carrying invocation tool call result state data.
/// </summary>
public sealed record InvocationToolCallResultState(
    string RequestId,
    bool Succeeded,
    string Result,
    string? Error,
    DateTimeOffset ResolvedAt);
