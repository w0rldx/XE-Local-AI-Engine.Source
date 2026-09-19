namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed class InvocationApprovalResolutionState
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }

    public required DateTimeOffset ResolvedAt { get; init; }
}
