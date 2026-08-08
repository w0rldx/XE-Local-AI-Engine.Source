namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows.Implementation;

using Microsoft.Agents.AI.Workflows;

/// <summary>
///     Side-channel event a Debug-print node emits via <c>IWorkflowContext.AddEventAsync</c>. It carries the upstream
///     payload the Debug node tapped WITHOUT routing it down the edge (the node also forwards the payload unchanged as
///     its return value, so the edge does NOT fork). The drain maps this to a
///     <see cref="PreviewWorkflowUpdateKind.NodeDebug" /> update.
/// </summary>
internal sealed class PreviewDebugEvent : WorkflowEvent
{
    public PreviewDebugEvent(string nodeId, string payload)
        : base(payload)
    {
        NodeId = nodeId;
        Payload = payload;
    }

    public string NodeId { get; }

    public string Payload { get; }
}
