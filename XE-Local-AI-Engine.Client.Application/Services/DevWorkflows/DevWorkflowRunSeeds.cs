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

    /// <summary>
    ///     <paramref name="enabledRuleSets" /> is read once by the CALLER, which is the only thing here that touches
    ///     the store — keeping this composition static and testable, exactly as <c>maxNodeRunsPerRun</c> is passed in
    ///     as a plain value rather than looked up.
    /// </summary>
    public static IReadOnlyList<DevWorkflowNodeRunSeed> Compose(DevWorkflowGraph graph,
        DevWorkflowWorkItemSnapshot workItem,
        string? inputsJson,
        int maxNodeRunsPerRun,
        IReadOnlyList<DevWorkflowRuleSetSnapshot> enabledRuleSets)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(enabledRuleSets);

        var entryKeys = graph.EntryNodeKeys.Where(key => !graph.TemplateKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);

        // The operator's request has to reach the first agent, and there is no run-level input column: every ENTRY node
        // run is seeded with it, and the objective composer renders it at the top.
        var entryInput = JsonSerializer.Serialize(new EntryInput(workItem.Request, inputsJson), JsonOptions);
        var seeds = graph.Nodes.Values.Where(node => !graph.TemplateKeys.Contains(node.NodeKey))
                         .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                         .Select(node => new DevWorkflowNodeRunSeed(Guid.NewGuid(),
                             node.NodeKey,
                             node.NodeType,
                             node.MaxAttempts,
                             node.AgentDefinitionId,
                             workItem.DevelopmentProjectId,
                             entryKeys.Contains(node.NodeKey) ? entryInput : null,

                             // Recorded on EVERY node run, not only the entry ones and not only the agent ones: the
                             // resolution is what the node-run detail answers "which rules applied" with, and a row
                             // that skipped it would read as "none did".
                             DevWorkflowRulePolicyResolver.Compose(enabledRuleSets, workItem.DevelopmentProjectId, node.NodeType)))
                         .ToList();

        return seeds.Count > maxNodeRunsPerRun
            ? throw new DevWorkflowValidationException($"This definition has {seeds.Count} nodes, more than the {maxNodeRunsPerRun} node runs a run may carry.")
            : seeds;
    }

    private sealed record EntryInput(string WorkItemRequest, string? InputsJson);
}
