namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What a node run inherits: the latest version of every artifact its nearest producing ancestors made.</summary>
/// <remarks>
///     Recorded as consumed the moment the node run is handed them, rather than derived on read later — by then the
///     graph may have been rewritten. See docs/wiki/25-dev-workflows.md ("Consumption records").
/// </remarks>
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

        var artifacts = await store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken);
        return [.. artifacts.Where(artifact => artifact.IsLatest && sources.Contains(artifact.ProducingNodeKey))];
    }

    /// <summary>The same nearest producing ancestors, but the ones whose node run was SKIPPED, oldest first.</summary>
    /// <remarks>
    ///     Empty whenever nothing upstream was skipped. A skipped producer contributes no artifact, so a consumer not
    ///     told which slice was excused judges a partial result as if it were the whole one. Read off the node-run rows
    ///     rather than off a synthetic <see cref="DevWorkflowArtifactSnapshot" />, which would put a document in the
    ///     audit that no node produced. See docs/wiki/25-dev-workflows.md ("Consumption records").
    /// </remarks>
    public static async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> SkippedAsync(IDevWorkflowStore store,
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

        var nodeRuns = await store.ListNodeRunsAsync(runId, cancellationToken);
        return
        [
            .. nodeRuns.Where(nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Skipped && sources.Contains(nodeRun.NodeKey))
                       .OrderBy(static nodeRun => nodeRun.Sequence)
        ];
    }

    /// <summary>The nearest ancestors that can have produced anything, walking back through the ones that cannot.</summary>
    /// <remarks>
    ///     Only the three work types produce artifacts; a routing node has no output of its own to stop the walk with.
    ///     It is the TYPE that ends the walk on its own path, not evidence of output, so an author who wants a node to
    ///     see something two producers back draws the edge. The graph is acyclic and every edge names a declared node,
    ///     so this terminates and needs no depth bound.
    ///     See docs/wiki/25-dev-workflows.md ("Seeded templates") and ("The clones, the join, and the zero-task case").
    /// </remarks>
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

    /// <summary>Resolves and records them in one step, and answers what was recorded.</summary>
    /// <remarks>
    ///     The answer lets a caller put the same list in an objective. A node with nothing upstream records nothing:
    ///     the store rejects an empty use list rather than writing an event saying a node consumed no artifacts, which
    ///     every entry node would.
    /// </remarks>
    public static async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> RecordAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var upstream = await ResolveAsync(store, graph, run.Id, nodeRun.NodeKey, cancellationToken);
        if (upstream.Count == 0)
        {
            return upstream;
        }

        _ = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "consume-upstream"),
                ArtifactIds = [.. upstream.Select(static artifact => artifact.Id)]
            },
            cancellationToken);
        return upstream;
    }
}
