namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

public static class GraphWorkflowHubEvents
{
    public const string Changed = "graphWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is lowercase on the wire — the client switches on the literal. The payload deliberately
///     carries no content: the subscriber re-reads the named feed from its own watermark, so a dropped push degrades
///     to a late read rather than to a wrong render.
/// </remarks>
public sealed class GraphWorkflowChanged
{
    public required Guid RunId { get; init; }

    public required long Seq { get; init; }

    public required string Kind { get; init; }
}

public sealed class GraphWorkflowRunSubscriptionSnapshot
{
    public required Guid RunId { get; init; }

    public required string Status { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    /// <summary>Parked rows waiting on an operator's Approve/Reject (a <c>Pause</c>).</summary>
    public required int PendingDecisions { get; init; }

    /// <summary>Parked rows waiting on the chat user's answer (a <c>ChatInput</c>).</summary>
    public required int PendingInputs { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<GraphWorkflowRunEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}
