namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Three mistakes the parser accepts and a run then trips over. The first two are errors that refuse validate, save
///     and import; the third is a warning. A stored graph is not re-checked, so one saved earlier still runs.
/// </summary>
/// <remarks>
///     Errors: an <c>All</c> join fed only by branches of one decision node, two of them exclusive (always skipped); an
///     <c>upstream.&lt;key&gt;</c> binding to a node that is not an immediate predecessor (missing, the node fails). Warning:
///     an <c>input.&lt;not a predecessor&gt;</c> binding where several predecessors can succeed (a map by key). Narrow on
///     purpose, see <see cref="Exclusive" />. The workflow designer treats every finding as must-fix.
///     Rules: docs/wiki/21-graph-workflows.md ("Whole-graph rules").
/// </remarks>
internal static class GraphWorkflowSemanticLint
{
    /// <summary>The warnings, adding the errors to <paramref name="errors" />.</summary>
    public static IReadOnlyList<GraphWorkflowValidationError> Check(GraphWorkflowGraph graph, List<GraphWorkflowValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(errors);

        var warnings = new List<GraphWorkflowValidationError>();
        foreach (var node in graph.Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal))
        {
            if (node.JoinPolicy == GraphWorkflowJoinPolicy.All && ExclusiveBranchSource(graph, node, everyPair: false) is { } decision)
            {
                errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                    $"Node '{node.NodeKey}' waits for every inbound edge (joinPolicy 'All'), but they come from mutually exclusive branches of '{decision}', "
                    + "so one of them never fires and the node is always skipped. Set its joinPolicy to 'Any'."));
            }

            var predecessors = graph.InboundEdges(node.NodeKey).Select(static edge => edge.From).ToHashSet(StringComparer.Ordinal);

            // 'input' is one predecessor's output only when exactly one can succeed: an Any join fed solely by exclusive branches.
            var inputIsTheMap = predecessors.Count > 1
                                && (node.JoinPolicy == GraphWorkflowJoinPolicy.All || ExclusiveBranchSource(graph, node, everyPair: true) is null);
            foreach (var (member, path) in BindingPaths(node.Config))
            {
                var segments = path.Split('.');
                if (segments is ["upstream", var source, ..] && !predecessors.Contains(source))
                {
                    errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                        $"Node '{node.NodeKey}' reads '{path}' in {member}, but '{source}' has no edge into '{node.NodeKey}'. The 'upstream' map holds only "
                        + "immediate predecessors, so the path is missing at run time and the node fails. Add an edge from "
                        + $"'{source}' to '{node.NodeKey}', or read the value through a node that has one."));
                }
                else if (inputIsTheMap && segments is ["input", var next, ..] && !predecessors.Contains(next))
                {
                    // 'input' alone, or keyed by a predecessor, reads that map correctly; only a one-output path like 'input.output.text' misses.
                    var why = node.JoinPolicy == GraphWorkflowJoinPolicy.All ? "joinPolicy 'All'" : "more than one of them can succeed";
                    warnings.Add(new GraphWorkflowValidationError(node.NodeKey,
                        $"Node '{node.NodeKey}' reads '{path}' in {member}, but it has {predecessors.Count} predecessors and {why}, so 'input' is "
                        + "the map of its predecessors' outputs keyed by node key; read 'input.<nodeKey>.…' or 'upstream.<nodeKey>.…'."));
                }
            }
        }

        return warnings;
    }

    /// <summary>Every dot path a node resolves against its input document, with the config member that holds it.</summary>
    private static IEnumerable<BindingPath> BindingPaths(GraphWorkflowNodeConfig config) =>
        config switch
        {
            GraphWorkflowLlmCallConfig llm => llm.InputBindings.Select(static binding => new BindingPath($"inputBindings.{binding.Key}", binding.Value)),
            GraphWorkflowDecisionModelConfig decision => decision.InputBindings.Select(static binding => new BindingPath($"inputBindings.{binding.Key}", binding.Value)),
            GraphWorkflowToolConfig tool => tool.ArgumentBindings.Select(static binding => new BindingPath($"argumentBindings.{binding.Key}", binding.Value)),
            GraphWorkflowEndConfig { ResultPath: { } resultPath } => [new BindingPath("resultPath", resultPath)],
            _ => []
        };

    /// <summary>The decision node whose branches feed every inbound edge, with some pair (or <paramref name="everyPair" />) exclusive; else <see langword="null" />.</summary>
    /// <remarks>
    ///     An inbound edge lies on a branch when it IS that out-edge of the decision, or when its source is reached through
    ///     that out-edge only and through no path that bypasses the decision. An edge that fits no branch, or more than one,
    ///     clears the node: something outside the decision can still satisfy it.
    /// </remarks>
    private static string? ExclusiveBranchSource(GraphWorkflowGraph graph, GraphWorkflowGraphNode node, bool everyPair)
    {
        var inbound = graph.InboundEdges(node.NodeKey);
        if (inbound.Count < 2)
        {
            return null;
        }

        var decisions = graph.Nodes.Values
                             .Where(static candidate => candidate.Kind is GraphWorkflowNodeKind.Condition or GraphWorkflowNodeKind.DecisionModel)
                             .Select(static candidate => candidate.NodeKey)
                             .Order(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            var branches = graph.OutboundEdges(decision);
            if (branches.Count < 2)
            {
                continue;
            }

            var bypass = ReachableAvoiding(graph, decision);
            var reach = branches.ToDictionary(static branch => branch.Key,
                branch => new HashSet<string>(graph.Descendants(branch.To), StringComparer.Ordinal)
                {
                    branch.To
                },
                StringComparer.Ordinal);

            var taken = inbound.Select(edge => BranchOf(edge, decision, branches, bypass, reach)).ToList();
            if (taken.TrueForAll(static branch => branch is not null) && (everyPair ? EveryPairExclusive(inbound, taken!) : taken.Exists(first => taken.Exists(second => Exclusive(first!, second!)))))
            {
                return decision;
            }
        }

        return null;
    }

    /// <summary>Whether no two inbound edges from different sources can both fire: at most one predecessor ever succeeds.</summary>
    private static bool EveryPairExclusive(IReadOnlyList<GraphWorkflowGraphEdge> inbound, List<GraphWorkflowGraphEdge?> taken)
    {
        for (var first = 0; first < inbound.Count; first++)
        {
            for (var second = first + 1; second < inbound.Count; second++)
            {
                if (inbound[first].From != inbound[second].From && !Exclusive(taken[first]!, taken[second]!))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The out-edge of <paramref name="decision" /> whose branch <paramref name="edge" /> lies on, or <see langword="null" /> when none or several.</summary>
    private static GraphWorkflowGraphEdge? BranchOf(GraphWorkflowGraphEdge edge,
        string decision,
        IReadOnlyList<GraphWorkflowGraphEdge> branches,
        HashSet<string> bypass,
        Dictionary<string, HashSet<string>> reach)
    {
        if (edge.From == decision)
        {
            return edge;
        }

        if (bypass.Contains(edge.From))
        {
            return null;
        }

        var on = branches.Where(branch => reach[branch.Key].Contains(edge.From)).Take(2).ToList();
        return on.Count == 1 ? on[0] : null;
    }

    /// <summary>Every node the entry reaches without passing through <paramref name="avoided" />.</summary>
    private static HashSet<string> ReachableAvoiding(GraphWorkflowGraph graph, string avoided)
    {
        var seen = new HashSet<string>(graph.EntryNodeKeys.Where(key => key != avoided), StringComparer.Ordinal);
        var pending = new Stack<string>(seen);
        while (pending.Count > 0)
        {
            foreach (var edge in graph.OutboundEdges(pending.Pop()).Where(edge => edge.To != avoided && seen.Add(edge.To)))
            {
                pending.Push(edge.To);
            }
        }

        return seen;
    }

    /// <summary>Whether two out-edges of one node can never both fire on the same output document.</summary>
    /// <remarks>
    ///     <c>Eq</c> pairs count only over non-numbers or integers that fit a long: the runtime compares other numbers
    ///     through decimal or double, so two distinct values (0.1…01 and 0.1…02, or 1.5e30 and 1.5000…1e30) can both match.
    /// </remarks>
    private static bool Exclusive(GraphWorkflowGraphEdge first, GraphWorkflowGraphEdge second)
    {
        if (first.Condition is not { } a || second.Condition is not { } b || !string.Equals(a.Path, b.Path, StringComparison.Ordinal))
        {
            return false;
        }

        return (a.Operator, b.Operator) switch
        {
            (GraphWorkflowConditionOperator.Eq, GraphWorkflowConditionOperator.Eq) => ComparedExactly(a.Value) && ComparedExactly(b.Value) && !JsonElement.DeepEquals(a.Value, b.Value),
            (GraphWorkflowConditionOperator.Exists, GraphWorkflowConditionOperator.NotExists) => true,
            (GraphWorkflowConditionOperator.NotExists, GraphWorkflowConditionOperator.Exists) => true,
            _ => false
        };
    }

    private static bool ComparedExactly(JsonElement value) =>
        value.ValueKind != JsonValueKind.Number || value.TryGetInt64(out _);

    private readonly record struct BindingPath(string Member, string Path);
}
