namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Value object carrying invocation approval resolution state data.
/// </summary>
public sealed record InvocationApprovalResolutionState(
    string RequestId,
    bool Approved,
    DateTimeOffset ResolvedAt);
