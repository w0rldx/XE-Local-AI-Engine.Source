namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What a node run inherits from the work before it: the latest version of every artifact its nearest producing
///     ancestors produced.
///     <para>
///         Recorded as consumed the moment the node run is handed them, rather than derived on read later. The record is
///         what the gate panel renders as its evidence list, what staleness propagation reads when an upstream artifact
///         is superseded, and what makes "this decision was taken on THAT plan" answerable a month afterwards — none of
///         which a graph walk at read time can reconstruct, because by then the graph may have been rewritten.
///     </para>
/// </summary>
internal static class DevWorkflowUpstreamArtifacts
{
    /// <summary>The latest artifacts of the node's nearest producing ancestors, oldest first. Empty for an entry node.</summary>
    public static async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ResolveAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        Guid runId,
        string nodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graph);

        var sources = ProducingAncestors(graph, nodeKey);
        if (sources.Count == 0)
        {
            return [];
        }

        var artifacts = await store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        return [.. artifacts.Where(artifact => artifact.IsLatest && sources.Contains(artifact.ProducingNodeKey))];
    }

    /// <summary>
    ///     The nearest ancestors that can have produced anything, walking back through the ones that cannot.
    ///     <para>
    ///         Only the three work types produce artifacts; a HumanGate, Gate, Parallel or Join is a routing decision
    ///         with no output of its own, so stopping at the immediate predecessors would hand a node behind one an
    ///         empty inheritance — the seeded template's decompose node sits behind a gate and its verify node behind a
    ///         join, and both were told to transform a plan they were never given.
    ///     </para>
    ///     <para>
    ///         A producing node ENDS the walk on its own path: what it produced is the current version of the work
    ///         further back, and continuing past it would hand the consumer the superseded input beside the output. The
    ///         graph is acyclic and every edge names a declared node, so this terminates and needs no depth bound.
    ///     </para>
    /// </summary>
    private static HashSet<string> ProducingAncestors(DevWorkflowGraph graph, string nodeKey)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            nodeKey
        };
        var pending = new Stack<string>();
        pending.Push(nodeKey);
        while (pending.Count > 0)
        {
            foreach (var key in graph.InboundEdges(pending.Pop()).Select(static edge => edge.From).Where(key => seen.Add(key)))
            {
                if (graph.Nodes.TryGetValue(key, out var from) && Produces(from.NodeType))
                {
                    _ = sources.Add(key);
                }
                else
                {
                    pending.Push(key);
                }
            }
        }

        return sources;
    }

    private static bool Produces(DevWorkflowNodeType nodeType) =>
        nodeType is DevWorkflowNodeType.Agent or DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask;

    /// <summary>
    ///     Resolves and records them in one step, and answers what was recorded so a caller can put the same list in an
    ///     objective. A node with nothing upstream records nothing — the store rejects an empty use list rather than
    ///     writing an event that says a node consumed no artifacts, which every entry node would.
    /// </summary>
    public static async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> RecordAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var upstream = await ResolveAsync(store, graph, run.Id, nodeRun.NodeKey, cancellationToken).ConfigureAwait(false);
        if (upstream.Count == 0)
        {
            return upstream;
        }

        _ = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "consume-upstream"),
                               [.. upstream.Select(static artifact => artifact.Id)]),
                           cancellationToken)
                       .ConfigureAwait(false);
        return upstream;
    }
}
