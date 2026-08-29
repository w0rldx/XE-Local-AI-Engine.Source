namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The node runs a graph gets at run start.
///     <para>
///         EVERY node except a materialization template gets a row up front, not just the entry ones. Creating them as
///         their branches settle reads well until terminalization: a run whose remaining rows do not exist yet has
///         "nothing live" and completes before it has run anything. A row that does not exist is still the right answer
///         for a decomposition's children — which is why an absent source reads as a pending edge — but for a graph
///         known at run start there is nothing to wait for.
///     </para>
///     <para>
///         Shared by the run service and the dispatcher rather than duplicated: the service composes them because it is
///         the only thing holding the caller's inputs, and the dispatcher composes them for a run created any other way.
///     </para>
/// </summary>
internal static class DevWorkflowRunSeeds
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<DevWorkflowNodeRunSeed> Compose(DevWorkflowGraph graph,
        DevWorkflowWorkItemSnapshot workItem,
        string? inputsJson,
        int maxNodeRunsPerRun)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(workItem);

        var templateKeys = graph.Nodes.Values.Where(static node => node.Materialization is not null)
                                .Select(static node => node.Materialization!.TemplateNodeKey)
                                .ToHashSet(StringComparer.Ordinal);
        var entryKeys = graph.EntryNodeKeys.Where(key => !templateKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);

        // The operator's request has to reach the first agent, and there is no run-level input column: every ENTRY node
        // run is seeded with it, and the objective composer renders it at the top.
        var entryInput = JsonSerializer.Serialize(new EntryInput(workItem.Request, inputsJson), JsonOptions);
        var seeds = graph.Nodes.Values.Where(node => !templateKeys.Contains(node.NodeKey))
                         .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                         .Select(node => new DevWorkflowNodeRunSeed(Guid.NewGuid(),
                             node.NodeKey,
                             node.NodeType,
                             node.MaxAttempts,
                             node.AgentDefinitionId,
                             workItem.DevelopmentProjectId,
                             entryKeys.Contains(node.NodeKey) ? entryInput : null))
                         .ToList();

        return seeds.Count > maxNodeRunsPerRun
            ? throw new DevWorkflowValidationException($"This definition has {seeds.Count} nodes, more than the {maxNodeRunsPerRun} node runs a run may carry.")
            : seeds;
    }

    private sealed record EntryInput(string WorkItemRequest, string? InputsJson);
}
