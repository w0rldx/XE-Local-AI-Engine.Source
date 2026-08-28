namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What a node run inherits from the nodes immediately before it: the latest version of every artifact they
///     produced.
///     <para>
///         Recorded as consumed the moment the node run is handed them, rather than derived on read later. The record is
///         what the gate panel renders as its evidence list, what staleness propagation reads when an upstream artifact
///         is superseded, and what makes "this decision was taken on THAT plan" answerable a month afterwards — none of
///         which a graph walk at read time can reconstruct, because by then the graph may have been rewritten.
///     </para>
/// </summary>
internal static class DevWorkflowUpstreamArtifacts
{
    /// <summary>The latest artifacts of the node's immediate predecessors, oldest first. Empty for an entry node.</summary>
    public static async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ResolveAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        Guid runId,
        string nodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graph);

        var sources = graph.InboundEdges(nodeKey).Select(static edge => edge.From).ToHashSet(StringComparer.Ordinal);
        if (sources.Count == 0)
        {
            return [];
        }

        var artifacts = await store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        return [.. artifacts.Where(artifact => artifact.IsLatest && sources.Contains(artifact.ProducingNodeKey))];
    }

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
