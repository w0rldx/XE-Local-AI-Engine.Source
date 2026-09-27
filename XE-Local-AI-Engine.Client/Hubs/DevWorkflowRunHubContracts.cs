namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

public static class DevWorkflowHubEvents
{
    public const string Changed = "devWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is lowercase on the wire — the client switches on the literal. The payload deliberately
///     carries no content: the subscriber re-reads the named feed from its own watermark, so a dropped push degrades
///     to a late read rather than to a wrong render.
/// </remarks>
public sealed class DevWorkflowChanged
{
    public required Guid RunId { get; init; }

    public required long Seq { get; init; }

    public required string Kind { get; init; }
}

public sealed class DevWorkflowRunSubscriptionSnapshot
{
    public required Guid RunId { get; init; }

    public required string Status { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<DevWorkflowRunEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}
