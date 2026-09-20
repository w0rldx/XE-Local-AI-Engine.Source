namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Which scoped rule sets apply to a node run, and the record of that decision.</summary>
/// <remarks>
///     The predicate is stated here once: both axes must match, an EMPTY axis matches everything, and every match is
///     applied. Resolution is recorded on every node type; the bodies are injected by two,
///     <c>DevWorkflowAgentExecutor.ComposeObjectiveAsync</c> and <c>DevWorkflowDevTaskExecutor</c>.
///     See docs/wiki/25-dev-workflows.md ("Policy injection").
/// </remarks>
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

    /// <summary>What a node-run's <c>policy_resolution_json</c> is written with, or null when nothing applied.</summary>
    /// <remarks>
    ///     Null is the honest answer for "no rule set matched", and keeps an untouched column from claiming an empty
    ///     resolution. The BODY is snapshotted for the node types that inject it, and only those.
    ///     See docs/wiki/25-dev-workflows.md ("Policy injection").
    /// </remarks>
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

    /// <summary>Whether a node type renders policy text into what it dispatches.</summary>
    /// <remarks>
    ///     The agent lane's objective and the DevTask lane's coder and reviewer prompts do. A Tool node has no prose
    ///     channel to put a body in and a HumanGate asks a person; both still RECORD which rule sets applied.
    /// </remarks>
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

    /// <summary>A stored scope with each axis normalised to a list, or NULL when the column cannot be read at all.</summary>
    /// <remarks>
    ///     The two states are kept apart because the safe answer differs by caller: the resolver treats an unreadable
    ///     scope as matching NOTHING, a read model renders it as empty axes.
    ///     See docs/wiki/25-dev-workflows.md ("Policy injection").
    /// </remarks>
    public static DevWorkflowRuleSetScope? ReadScope(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredScope>(scopeJson, JsonOptions) is { } scope
                ? new DevWorkflowRuleSetScope { ProjectIds = scope.ProjectIds ?? [], NodeTypes = scope.NodeTypes ?? [] }
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
public sealed class DevWorkflowRuleSetScope
{
    public required IReadOnlyList<Guid> ProjectIds { get; init; }

    public required IReadOnlyList<string> NodeTypes { get; init; }
}

/// <summary>One rule set as a node-run records it: which document applied, under what name, at which exact text.</summary>
/// <remarks>
///     <see cref="Body" /> is that text, and is nullable only to keep the reader honest about rows written before it
///     existed. It never reaches the wire. See docs/wiki/25-dev-workflows.md ("Policy injection").
/// </remarks>
public sealed record DevWorkflowAppliedRuleSet(Guid Id, string Name, string ContentSha256, string? Body);
