namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed record InvocationApprovalState(
    string RequestId,
    string Description,
    DateTimeOffset RequestedAt);
