namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Validates a <see cref="PreviewWorkflowGraph" /> against the basic-variant rules (plan §7.3). A workflow is a
///     STRICTLY LINEAR chain: exactly one Start, exactly one reachable End, in-degree ≤ 1 and out-degree ≤ 1 per node
///     (acyclic alone is insufficient — that still permits fan-out → parallel supersteps, invariant #5), every Agent
///     node has a model + instructions, and at least one Agent node lies between Start and End (a Start→End chain with
///     no agent is a 400, never a no-op). Pure/stateless so it is trivially unit-testable.
/// </summary>
public static class PreviewWorkflowGraphValidator
{
    public static PreviewWorkflowValidationResult Validate(PreviewWorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var errors = new List<string>();

        var nodes = graph.Nodes;
        var edges = graph.Edges;

        if (nodes.Count == 0)
        {
            errors.Add("The workflow must contain at least a Start and an End node.");
            return PreviewWorkflowValidationResult.Invalid(errors);
        }

        // Node ids must be unique and non-empty — every downstream rule keys on the id.
        var nodesById = new Dictionary<string, PreviewWorkflowGraphNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                errors.Add("Every node must have a non-empty id.");
                continue;
            }

            if (!nodesById.TryAdd(node.Id, node))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }

        var startNodes = nodes.Where(static n => n.Kind == PreviewWorkflowNodeKind.Start).ToList();
        var endNodes = nodes.Where(static n => n.Kind == PreviewWorkflowNodeKind.End).ToList();

        if (startNodes.Count != 1)
        {
            errors.Add($"The workflow must have exactly one Start node (found {startNodes.Count}).");
        }

        if (endNodes.Count != 1)
        {
            errors.Add($"The workflow must have exactly one End node (found {endNodes.Count}).");
        }

        // Every edge must reference known nodes before we can reason about degrees / reachability.
        foreach (var edge in edges)
        {
            if (!nodesById.ContainsKey(edge.SourceId))
            {
                errors.Add($"Edge references unknown source node '{edge.SourceId}'.");
            }

            if (!nodesById.ContainsKey(edge.TargetId))
            {
                errors.Add($"Edge references unknown target node '{edge.TargetId}'.");
            }
        }

        // Linearity: in-degree ≤ 1 AND out-degree ≤ 1 per node. This (combined with single Start/End) forces a single
        // chain — it rejects fan-out (which acyclicity alone would allow) and fan-in.
        // Key the degree maps off the deduplicated, non-empty ids (nodesById) so duplicate/empty ids — already reported
        // above — do not throw here.
        var inDegree = nodesById.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var outDegree = nodesById.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var adjacency = nodesById.Keys.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (outDegree.TryGetValue(edge.SourceId, out var sourceOut))
            {
                outDegree[edge.SourceId] = sourceOut + 1;
                adjacency[edge.SourceId].Add(edge.TargetId);
            }

            if (inDegree.TryGetValue(edge.TargetId, out var targetIn))
            {
                inDegree[edge.TargetId] = targetIn + 1;
            }
        }

        foreach (var (nodeId, outCount) in outDegree)
        {
            if (outCount > 1)
            {
                errors.Add($"Node '{nodeId}' has out-degree {outCount}; the workflow must be linear (out-degree ≤ 1).");
            }
        }

        foreach (var (nodeId, inCount) in inDegree)
        {
            if (inCount > 1)
            {
                errors.Add($"Node '{nodeId}' has in-degree {inCount}; the workflow must be linear (in-degree ≤ 1).");
            }
        }

        // Every Agent node must carry a model and instructions (the privacy-sensitive run payload).
        foreach (var agent in nodes.Where(static n => n.Kind == PreviewWorkflowNodeKind.Agent))
        {
            if (string.IsNullOrWhiteSpace(agent.Model))
            {
                errors.Add($"Agent node '{agent.Id}' must specify a model.");
            }

            if (string.IsNullOrWhiteSpace(agent.Instructions))
            {
                errors.Add($"Agent node '{agent.Id}' must specify instructions.");
            }
        }

        // The Start node must carry non-empty seed text (the first agent's user input); an empty run is rejected.
        if (string.IsNullOrWhiteSpace(graph.StartText))
        {
            errors.Add("The Start node must have non-empty input text.");
        }

        // The reachability + "≥ 1 Agent between Start and End" rules only make sense once the structural rules hold.
        if (errors.Count == 0)
        {
            ValidateReachableAgentPath(startNodes[0], endNodes[0], nodesById, adjacency, errors);
        }

        return errors.Count == 0 ? PreviewWorkflowValidationResult.Valid : PreviewWorkflowValidationResult.Invalid(errors);
    }

    private static void ValidateReachableAgentPath(PreviewWorkflowGraphNode start,
        PreviewWorkflowGraphNode end,
        IReadOnlyDictionary<string, PreviewWorkflowGraphNode> nodesById,
        IReadOnlyDictionary<string, List<string>> adjacency,
        List<string> errors)
    {
        // Walk the (now-proven-linear) chain from Start. With out-degree ≤ 1 each node has at most one successor, so a
        // simple forward walk reaches every node on the path; track visited as a cycle guard.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = start.Id;
        var reachedEnd = false;
        var agentCount = 0;

        while (current is not null && visited.Add(current))
        {
            if (!nodesById.TryGetValue(current, out var node))
            {
                break;
            }

            if (node.Kind == PreviewWorkflowNodeKind.Agent)
            {
                agentCount++;
            }

            if (node.Id == end.Id)
            {
                reachedEnd = true;
                break;
            }

            var successors = adjacency[current];
            current = successors.Count == 1 ? successors[0] : null;
        }

        if (!reachedEnd)
        {
            errors.Add("The End node must be reachable from the Start node along the linear chain.");
            return;
        }

        if (agentCount == 0)
        {
            errors.Add("The workflow must contain at least one Agent node between Start and End.");
        }
    }
}
