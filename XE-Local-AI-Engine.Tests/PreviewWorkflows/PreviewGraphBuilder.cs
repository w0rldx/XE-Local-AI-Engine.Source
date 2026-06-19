namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>Small builder for valid/invalid preview graphs in tests. The "linear" factory is a valid baseline.</summary>
internal static class PreviewGraphBuilder
{
    public static PreviewWorkflowGraphNode Start(string id = "start")
    {
        return new PreviewWorkflowGraphNode
        {
            Id = id,
            Kind = PreviewWorkflowNodeKind.Start
        };
    }

    public static PreviewWorkflowGraphNode End(string id = "end")
    {
        return new PreviewWorkflowGraphNode
        {
            Id = id,
            Kind = PreviewWorkflowNodeKind.End
        };
    }

    public static PreviewWorkflowGraphNode Agent(string id, string model = "qwen3.5:0.8b", string instructions = "Respond.")
    {
        return new PreviewWorkflowGraphNode
        {
            Id = id,
            Kind = PreviewWorkflowNodeKind.Agent,
            Label = id,
            Model = model,
            Instructions = instructions
        };
    }

    public static PreviewWorkflowGraphNode Debug(string id)
    {
        return new PreviewWorkflowGraphNode
        {
            Id = id,
            Kind = PreviewWorkflowNodeKind.Debug
        };
    }

    public static PreviewWorkflowGraphNode Pause(string id)
    {
        return new PreviewWorkflowGraphNode
        {
            Id = id,
            Kind = PreviewWorkflowNodeKind.Pause
        };
    }

    public static PreviewWorkflowGraphEdge Edge(string source, string target)
    {
        return new PreviewWorkflowGraphEdge
        {
            SourceId = source,
            TargetId = target
        };
    }

    /// <summary>Start → agent → End — the canonical minimal valid graph.</summary>
    public static PreviewWorkflowGraph Linear(string startText = "hello")
    {
        return new PreviewWorkflowGraph
        {
            StartText = startText,
            Nodes = [Start(), Agent("agent"), End()],
            Edges = [Edge("start", "agent"), Edge("agent", "end")]
        };
    }
}
