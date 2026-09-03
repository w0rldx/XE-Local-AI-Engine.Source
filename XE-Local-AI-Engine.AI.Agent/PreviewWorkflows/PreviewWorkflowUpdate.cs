namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Provider-agnostic MAF stream event mirrored by the execution service and Client. The execution service adds the
///     <c>runId</c>; MAF types stay inside the runner/session. Wire mappings:
///     <see cref="PreviewWorkflowUpdateKind" /> values map to the hub events:
///     NodeStarted   → preview.node.started   (NodeId set)
///     NodeOutput    → preview.node.output    (NodeId + Output set)
///     NodeDebug     → preview.node.debug     (NodeId + Output = the upstream payload the Debug node tapped)
///     NodeFailed    → preview.node.failed    (NodeId + Error set)
///     RunPaused     → preview.run.paused     (NodeId = the Pause node, Output = upstream output to display,
///     RequestId = the pause token to pass back to resume)
///     RunCompleted  → preview.run.completed  (Output = the terminal workflow output)
///     RunFailed     → preview.run.failed     (Error set)
///     The execution service owns run.started/cancelled lifecycle events.
/// </summary>
public sealed record PreviewWorkflowUpdate
{
    public required PreviewWorkflowUpdateKind Kind { get; init; }

    public string? NodeId { get; init; }

    public string? Output { get; init; }

    /// <summary>Sanitized failure message for NodeFailed/RunFailed.</summary>
    public string? Error { get; init; }

    /// <summary>
    ///     For <see cref="PreviewWorkflowUpdateKind.RunPaused" />: the opaque pause token the caller passes to
    ///     <see cref="IPreviewWorkflowRunSession.ResumeAsync" /> to continue the held run.
    /// </summary>
    public string? RequestId { get; init; }

    public static PreviewWorkflowUpdate NodeStarted(string nodeId)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.NodeStarted,
            NodeId = nodeId
        };
    }

    public static PreviewWorkflowUpdate NodeOutput(string nodeId, string output)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.NodeOutput,
            NodeId = nodeId,
            Output = output
        };
    }

    public static PreviewWorkflowUpdate NodeDebug(string nodeId, string output)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.NodeDebug,
            NodeId = nodeId,
            Output = output
        };
    }

    public static PreviewWorkflowUpdate NodeFailed(string nodeId, string error)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.NodeFailed,
            NodeId = nodeId,
            Error = error
        };
    }

    public static PreviewWorkflowUpdate RunPaused(string nodeId, string output, string requestId)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.RunPaused,
            NodeId = nodeId,
            Output = output,
            RequestId = requestId
        };
    }

    public static PreviewWorkflowUpdate RunCompleted(string? output)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.RunCompleted,
            Output = output
        };
    }

    public static PreviewWorkflowUpdate RunFailed(string error)
    {
        return new PreviewWorkflowUpdate
        {
            Kind = PreviewWorkflowUpdateKind.RunFailed,
            Error = error
        };
    }
}

public enum PreviewWorkflowUpdateKind
{
    NodeStarted,
    NodeOutput,
    NodeDebug,
    NodeFailed,
    RunPaused,
    RunCompleted,
    RunFailed
}
