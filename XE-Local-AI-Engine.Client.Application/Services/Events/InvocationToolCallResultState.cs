namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed record InvocationToolCallResultState(
    string RequestId,
    bool Succeeded,
    string Result,
    string? Error,
    DateTimeOffset ResolvedAt);
