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
///         honest answer whichever node is asked. Two lanes INJECT the bodies: an Agent node's objective
///         (<c>DevWorkflowAgentExecutor.ComposeObjectiveAsync</c>) and a DevTask node's Dev Mode prompts, which reach
///         the coder and the reviewer through an event on the task (<c>DevWorkflowDevTaskExecutor</c>). A Tool node runs
///         a command profile with no prose channel at all and a HumanGate asks a person; both still record, and
///         injecting there is additive whenever those lanes grow a place to put it.
///     </para>
/// </summary>
public static class DevWorkflowRulePolicyResolver
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The rule sets that apply, in the order they are injected. The caller's list is already name-ordered by the
    ///     store, and that order is preserved rather than re-sorted.
    /// </summary>
    public static IReadOnlyList<DevWorkflowRuleSetSnapshot> Resolve(IReadOnlyList<DevWorkflowRuleSetSnapshot> enabledRuleSets,
        Guid? developmentProjectId,
        DevWorkflowNodeType nodeType)
    {
        ArgumentNullException.ThrowIfNull(enabledRuleSets);

        return [.. enabledRuleSets.Where(ruleSet => Matches(ruleSet, developmentProjectId, nodeType))];
    }

    /// <summary>
    ///     What a node-run's <c>policy_resolution_json</c> is written with, or null when nothing applied — which is the
    ///     honest answer for "no rule set matched" and keeps an untouched column from claiming an empty resolution.
    ///     <para>
    ///         The BODY is snapshotted for the node types that INJECT it, and only those. Re-reading the rule set at
    ///         dispatch would let an edit landing between materialization and dispatch hand the agent one text while
    ///         the audit permanently claimed another, and a delete leave nothing to inject at all. On every other node
    ///         type the text would be a copy nothing reads, decrypted into each node-run snapshot on every list — so
    ///         those rows record the id, the name and the hash, which is all their audit ever needed.
    ///     </para>
    /// </summary>
    public static string? Compose(IReadOnlyList<DevWorkflowRuleSetSnapshot> enabledRuleSets, Guid? developmentProjectId, DevWorkflowNodeType nodeType)
    {
        var matched = Resolve(enabledRuleSets, developmentProjectId, nodeType);
        var snapshotBodies = InjectsPolicyText(nodeType);
        return matched.Count == 0
            ? null
            : JsonSerializer.Serialize(matched.Select(ruleSet => new DevWorkflowAppliedRuleSet(ruleSet.Id,
                                                  ruleSet.Name,
                                                  ruleSet.ContentSha256,
                                                  snapshotBodies ? ruleSet.Body : null))
                                              .ToList(),
                JsonOptions);
    }

    /// <summary>
    ///     Whether a node type renders policy text into what it dispatches — the agent lane's objective and the DevTask
    ///     lane's coder and reviewer prompts. A Tool node runs a command profile with no prose channel at all, so it has
    ///     nowhere to put a body, and a HumanGate asks a person rather than a model. Both still RECORD which rule sets
    ///     applied.
    /// </summary>
    public static bool InjectsPolicyText(DevWorkflowNodeType nodeType) =>
        nodeType is DevWorkflowNodeType.Agent or DevWorkflowNodeType.DevTask;

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

    /// <summary>
    ///     A stored scope with each axis normalised to a list, or NULL when the column cannot be read at all.
    ///     <para>
    ///         The two states are kept apart on purpose, because the safe answer differs by caller: the resolver treats
    ///         an unreadable scope as matching NOTHING — the endpoints are its only validating writer, so an unreadable
    ///         one is a hand-edited row, and "applies to every node on this box" is the dangerous reading — while a
    ///         read model renders it as empty axes, because a management page that cannot LOAD the row is a page nobody
    ///         can use to fix it.
    ///     </para>
    /// </summary>
    public static DevWorkflowRuleSetScope? ReadScope(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredScope>(scopeJson, JsonOptions) is { } scope
                ? new DevWorkflowRuleSetScope(scope.ProjectIds ?? [], scope.NodeTypes ?? [])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Matches(DevWorkflowRuleSetSnapshot ruleSet, Guid? developmentProjectId, DevWorkflowNodeType nodeType)
    {
        if (ReadScope(ruleSet.ScopeJson) is not { } scope)
        {
            return false;
        }

        var projectMatches = scope.ProjectIds.Count == 0 || (developmentProjectId is { } projectId && scope.ProjectIds.Contains(projectId));
        var nodeTypeMatches = scope.NodeTypes.Count == 0 || scope.NodeTypes.Contains(nodeType.ToString(), StringComparer.OrdinalIgnoreCase);
        return projectMatches && nodeTypeMatches;
    }

    /// <summary>The stored document, whose axes may be absent — <see cref="ReadScope" /> is what normalises them.</summary>
    private sealed record StoredScope(IReadOnlyList<Guid>? ProjectIds, IReadOnlyList<string>? NodeTypes);
}

/// <summary>Where a rule set applies, with both axes present. An EMPTY axis matches everything.</summary>
public sealed record DevWorkflowRuleSetScope(IReadOnlyList<Guid> ProjectIds, IReadOnlyList<string> NodeTypes);

/// <summary>
///     One rule set as a node-run records it: which document applied, under what name, at which exact text — and that
///     text itself. The hash keeps the audit truthful after the rule set is edited or deleted; the snapshotted
///     <see cref="Body" /> is what the node was actually given, so the two can never tell different stories.
///     <para>
///         <see cref="Body" /> is nullable only to keep the reader honest about rows written before it existed. It
///         never reaches the wire: the node-run response carries the id, the name and the hashes, and a reader who
///         wants the text asks the rule set for it.
///     </para>
/// </summary>
public sealed record DevWorkflowAppliedRuleSet(Guid Id, string Name, string ContentSha256, string? Body);
