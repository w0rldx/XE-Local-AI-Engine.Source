namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Which scoped rule sets apply to a node run, and the record of that decision.
///     <para>
///         The predicate is Y2's, stated once: each of the two axes — <c>projectIds</c> and <c>nodeTypes</c> — matches
///         when it is EMPTY, and otherwise by exact case-insensitive membership; both must match; every match is
///         applied, ordered by name. No globs, no precedence, no conflict resolution — a rule set either applies or it
///         does not, and two that both apply are both injected.
///     </para>
///     <para>
///         Resolution is RECORDED on every node-run type, agent or not, so <c>appliedRuleSets</c> on a node-run is an
///         honest answer whichever node is asked. Only an Agent node's objective INJECTS the bodies today
///         (<c>DevWorkflowAgentExecutor.ComposeObjectiveAsync</c>): a DevTask node's prompt is Dev Mode's to compose
///         and a Tool node runs a command profile with no prose channel at all. Both still record, and injecting there
///         is additive whenever those lanes grow a place to put it.
///     </para>
/// </summary>
internal static class DevWorkflowRulePolicyResolver
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The rule sets that apply, in the order they are injected. The caller's list is already name-ordered by the
    ///     store, and that order is preserved rather than re-sorted.
    /// </summary>
    public static IReadOnlyList<DevWorkflowRuleSetSummary> Resolve(IReadOnlyList<DevWorkflowRuleSetSummary> enabledRuleSets,
        Guid? developmentProjectId,
        DevWorkflowNodeType nodeType)
    {
        ArgumentNullException.ThrowIfNull(enabledRuleSets);

        return [.. enabledRuleSets.Where(ruleSet => Matches(ruleSet, developmentProjectId, nodeType))];
    }

    /// <summary>
    ///     What a node-run's <c>policy_resolution_json</c> is written with, or null when nothing applied — which is the
    ///     honest answer for "no rule set matched" and keeps an untouched column from claiming an empty resolution.
    /// </summary>
    public static string? Compose(IReadOnlyList<DevWorkflowRuleSetSummary> enabledRuleSets, Guid? developmentProjectId, DevWorkflowNodeType nodeType)
    {
        var matched = Resolve(enabledRuleSets, developmentProjectId, nodeType);
        return matched.Count == 0
            ? null
            : JsonSerializer.Serialize(matched.Select(ruleSet => new DevWorkflowAppliedRuleSet(ruleSet.Id, ruleSet.Name, ruleSet.ContentSha256)).ToList(), JsonOptions);
    }

    /// <summary>
    ///     Reads a recorded resolution back. A column that will not parse answers empty rather than throwing: the
    ///     resolution is an AUDIT record, and failing a node's dispatch over an unreadable one would turn a bad row
    ///     into a stopped workflow.
    /// </summary>
    public static IReadOnlyList<DevWorkflowAppliedRuleSet> Read(string? policyResolutionJson)
    {
        if (string.IsNullOrWhiteSpace(policyResolutionJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<DevWorkflowAppliedRuleSet>>(policyResolutionJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Matches(DevWorkflowRuleSetSummary ruleSet, Guid? developmentProjectId, DevWorkflowNodeType nodeType)
    {
        RuleScope? scope;
        try
        {
            scope = JsonSerializer.Deserialize<RuleScope>(ruleSet.ScopeJson, JsonOptions);
        }
        catch (JsonException)
        {
            // A scope nothing can read is not a scope that matches everything. The endpoints validate the document on
            // the way in, so this is a hand-edited row, and the safe reading of an unreadable scope is "applies to
            // nothing" rather than "applies to every node on this box".
            return false;
        }

        if (scope is null)
        {
            return false;
        }

        var projectMatches = scope.ProjectIds is not { Count: > 0 } projectIds || (developmentProjectId is { } projectId && projectIds.Contains(projectId));
        var nodeTypeMatches = scope.NodeTypes is not { Count: > 0 } nodeTypes || nodeTypes.Contains(nodeType.ToString(), StringComparer.OrdinalIgnoreCase);
        return projectMatches && nodeTypeMatches;
    }

    private sealed record RuleScope(IReadOnlyList<Guid>? ProjectIds, IReadOnlyList<string>? NodeTypes);
}

/// <summary>
///     One rule set as a node-run records it: which document applied, under what name, at which exact text. The hash is
///     what keeps the audit truthful after the rule set is edited or deleted.
/// </summary>
internal sealed record DevWorkflowAppliedRuleSet(Guid Id, string Name, string ContentSha256);
