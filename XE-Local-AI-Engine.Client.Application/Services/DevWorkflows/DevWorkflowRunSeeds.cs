namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The node runs a graph gets at run start.</summary>
/// <remarks>
///     EVERY node except a materialization template gets a row up front, not just the entry ones: creating them as
///     their branches settle reads well until terminalization, where a run whose remaining rows do not exist has
///     "nothing live" and completes before running anything. A missing row is still right for a decomposition's
///     children — which is why an absent source reads as a pending edge — but a graph known at run start has nothing
///     to wait for. Composed in one place, <c>DevWorkflowRunService</c> at run creation, and static so it stays testable.
/// </remarks>
internal static class DevWorkflowRunSeeds
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="enabledRuleSets">Read once by the CALLER, the only thing here that touches the store.</param>
    /// <remarks>
    ///     That keeps this composition static and testable, exactly as <c>maxNodeRunsPerRun</c> being a plain value
    ///     rather than a lookup does.
    /// </remarks>
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
        var entryInput = JsonSerializer.Serialize(new EntryInput { WorkItemRequest = workItem.Request, InputsJson = inputsJson }, JsonOptions);
        var seeds = graph.Nodes.Values.Where(node => !graph.TemplateKeys.Contains(node.NodeKey))
                         .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                         .Select(node => new DevWorkflowNodeRunSeed
                         {
                             NodeRunId = Guid.NewGuid(),
                             NodeKey = node.NodeKey,
                             NodeType = node.NodeType,
                             MaxAttempts = node.MaxAttempts,
                             AgentDefinitionId = node.AgentDefinitionId,
                             DevelopmentProjectId = workItem.DevelopmentProjectId,
                             InputJson = entryKeys.Contains(node.NodeKey) ? entryInput : null,
                             // Recorded on EVERY node run, not only the entry or agent ones: the resolution is what the node-run detail answers "which rules
                             // applied" with, and a row that skipped it would read as "none did".
                             PolicyResolutionJson = DevWorkflowRulePolicyResolver.Compose(enabledRuleSets, workItem.DevelopmentProjectId, node.NodeType)
                         })
                         .ToList();

        return seeds.Count > maxNodeRunsPerRun
            ? throw new DevWorkflowValidationException($"This definition has {seeds.Count} nodes, more than the {maxNodeRunsPerRun} node runs a run may carry.")
            : seeds;
    }

    private sealed record EntryInput
    {
        public required string WorkItemRequest { get; init; }

        public required string? InputsJson { get; init; }
    }
}
