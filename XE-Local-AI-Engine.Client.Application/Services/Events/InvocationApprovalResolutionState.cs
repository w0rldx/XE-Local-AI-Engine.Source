namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed record InvocationApprovalResolutionState(
    string RequestId,
    bool Approved,
    DateTimeOffset ResolvedAt);
