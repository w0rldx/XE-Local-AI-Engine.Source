namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Provider-agnostic event the session emits as it drains the MAF stream. The execution service adds the
///     <c>runId</c> when it republishes these over the hub; the runner itself is run-agnostic (it maps ONE run's
///     events). NO Microsoft.Agents.AI types leak through this DTO (invariant: MAF stays inside the runner/session).
///
///     === EVENT SCHEMA (mirror in the execution service + Client) ===
///     <see cref="PreviewWorkflowUpdateKind" /> values map to the hub events:
///       NodeStarted   → preview.node.started   (NodeId set)
///       NodeOutput    → preview.node.output    (NodeId + Output set)
///       NodeDebug     → preview.node.debug     (NodeId + Output = the upstream payload the Debug node tapped)
///       NodeFailed    → preview.node.failed    (NodeId + Error set)
///       RunPaused     → preview.run.paused     (NodeId = the Pause node, Output = upstream output to display,
///                                               RequestId = the pause token to pass back to resume)
///       RunCompleted  → preview.run.completed  (Output = the terminal workflow output)
///       RunFailed     → preview.run.failed     (Error set)
///     run.started/cancelled are owned by the execution service (lifecycle), not surfaced by the runner drain.
/// </summary>
public sealed record PreviewWorkflowUpdate
{
    public required PreviewWorkflowUpdateKind Kind { get; init; }

    /// <summary>The node this update concerns; null for run-level updates that aren't node-scoped.</summary>
    public string? NodeId { get; init; }

    /// <summary>Output/text payload (node output, debug tap payload, pause display, terminal output).</summary>
    public string? Output { get; init; }

    /// <summary>Sanitized failure message for NodeFailed/RunFailed.</summary>
    public string? Error { get; init; }

    /// <summary>
    ///     For <see cref="PreviewWorkflowUpdateKind.RunPaused" />: the opaque pause token the caller passes to
    ///     <see cref="IPreviewWorkflowRunSession.ResumeAsync" /> to continue the held run.
    /// </summary>
    public string? RequestId { get; init; }

    public static PreviewWorkflowUpdate NodeStarted(string nodeId) =>
        new() { Kind = PreviewWorkflowUpdateKind.NodeStarted, NodeId = nodeId };

    public static PreviewWorkflowUpdate NodeOutput(string nodeId, string output) =>
        new() { Kind = PreviewWorkflowUpdateKind.NodeOutput, NodeId = nodeId, Output = output };

    public static PreviewWorkflowUpdate NodeDebug(string nodeId, string output) =>
        new() { Kind = PreviewWorkflowUpdateKind.NodeDebug, NodeId = nodeId, Output = output };

    public static PreviewWorkflowUpdate NodeFailed(string nodeId, string error) =>
        new() { Kind = PreviewWorkflowUpdateKind.NodeFailed, NodeId = nodeId, Error = error };

    public static PreviewWorkflowUpdate RunPaused(string nodeId, string output, string requestId) =>
        new() { Kind = PreviewWorkflowUpdateKind.RunPaused, NodeId = nodeId, Output = output, RequestId = requestId };

    public static PreviewWorkflowUpdate RunCompleted(string? output) =>
        new() { Kind = PreviewWorkflowUpdateKind.RunCompleted, Output = output };

    public static PreviewWorkflowUpdate RunFailed(string error) =>
        new() { Kind = PreviewWorkflowUpdateKind.RunFailed, Error = error };
}

/// <summary>Discriminator for <see cref="PreviewWorkflowUpdate" />.</summary>
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
