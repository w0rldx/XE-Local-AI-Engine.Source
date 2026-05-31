namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Value object carrying invocation approval state data.
/// </summary>
public sealed record InvocationApprovalState(
    string RequestId,
    string Description,
    DateTimeOffset RequestedAt);
