namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>How a node with more than one inbound edge waits. The whole of the join semantics.</summary>
internal enum DevWorkflowJoinPolicy
{
    All,
    Any
}

/// <summary>
///     What a Tool node does with the repository it names. A CONFIG field rather than a node type, because the seven
///     types are closed (Y6) and these two are the same lane doing the same thing to the same workspace — one asks the
///     project's command profile whether the result is good, the other asks Dev Mode's apply gate to let it out.
/// </summary>
internal enum DevWorkflowToolMode
{
    Validate,
    Apply
}

/// <summary>The decomposition template a node expands into. All four fields are load-bearing.</summary>
internal sealed record DevWorkflowMaterialization(string TemplateNodeKey, DevWorkflowArtifactKind ArtifactKind, string JoinNodeKey, int MaxChildren);

/// <summary>
///     One node of the parsed graph. Only what the runtime reads: the JSON schema also carries authoring fields
///     (<c>modelProfile</c>, <c>reasoningEffort</c>, <c>requiredCapabilities</c>) that nothing dispatches on in v1, and
///     parsing them into properties no code reads would be shape without meaning. Unknown properties survive in the
///     stored blob either way — this projection is not a re-serialization of it.
/// </summary>
internal sealed record DevWorkflowGraphNode(
    string NodeKey,
    DevWorkflowNodeType NodeType,
    string Label,
    Guid? AgentDefinitionId,
    string? AgentSeedSlug,
    string? Instructions,
    IReadOnlyList<string> ValidationCommandIds,
    DevWorkflowJoinPolicy JoinPolicy,
    int MaxAttempts,
    int RetryDelaySeconds,
    int? NodeTimeoutSeconds,
    string? RetryTarget,
    DevWorkflowMaterialization? Materialization,
    DevWorkflowToolMode ToolMode);

internal sealed record DevWorkflowGraphEdge(string From, string To, DevWorkflowCondition? Condition)
{
    public override string ToString() =>
        $"'{From}' → '{To}'";
}

/// <summary>
///     The parsed, in-memory projection of a run's pinned <c>graph_json</c> — the single source of routing truth. Never
///     persisted separately: there is no run-edge table, and materialization rewrites the run's own blob rather than
///     adding rows to one.
/// </summary>
internal sealed class DevWorkflowGraph
{
    private const int SupportedSchemaVersion = 1;

    /// <summary>Defaults for a node that names none. Human waits and inline decisions get one try; work gets three.</summary>
    private const int DefaultWorkNodeMaxAttempts = 3;

    /// <summary>
    ///     The most children one decomposition may expand into (P2 R5). The materialization transaction rewrites the
    ///     run's whole encrypted graph blob, so the width of a fan-out is the size of that write — bounded here, at the
    ///     one place a definition can ask for it, rather than discovered when a run tries to commit it.
    /// </summary>
    private const int MaxTemplateChildren = 20;

    private readonly Dictionary<string, List<DevWorkflowGraphEdge>> _inbound;
    private readonly Dictionary<string, List<DevWorkflowGraphEdge>> _outbound;

    private DevWorkflowGraph(IReadOnlyDictionary<string, DevWorkflowGraphNode> nodes, IReadOnlyList<DevWorkflowGraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _inbound = nodes.Keys.ToDictionary(key => key, _ => new List<DevWorkflowGraphEdge>(), StringComparer.Ordinal);
        _outbound = nodes.Keys.ToDictionary(key => key, _ => new List<DevWorkflowGraphEdge>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            _outbound[edge.From].Add(edge);
            _inbound[edge.To].Add(edge);
        }

        EntryNodeKeys = [.. nodes.Keys.Where(key => _inbound[key].Count == 0).OrderBy(key => key, StringComparer.Ordinal)];
        TemplateKeys = new HashSet<string>(nodes.Values.Where(static node => node.Materialization is not null).SelectMany(node => TemplateSubtree(node.Materialization!)),
            StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, DevWorkflowGraphNode> Nodes { get; }

    public IReadOnlyList<DevWorkflowGraphEdge> Edges { get; }

    /// <summary>
    ///     Nodes with no inbound edge. Start is implicit, so this is what "entry node" means — and it is also why a
    ///     materialization template node, which is deliberately unreachable, must be excluded before this is read as the
    ///     set to materialize at run start.
    /// </summary>
    public IReadOnlyList<string> EntryNodeKeys { get; }

    /// <summary>
    ///     Every node of every materialization template SUBTREE — each template root plus what it reaches short of its
    ///     join node. These are the nodes a run does NOT give a row to at start: they are clones-in-waiting, and one
    ///     given a row would wait forever on a source the run never instantiates.
    /// </summary>
    public IReadOnlySet<string> TemplateKeys { get; }

    public IReadOnlyList<DevWorkflowGraphEdge> InboundEdges(string nodeKey) =>
        _inbound.TryGetValue(nodeKey, out var edges) ? edges : [];

    public IReadOnlyList<DevWorkflowGraphEdge> OutboundEdges(string nodeKey) =>
        _outbound.TryGetValue(nodeKey, out var edges) ? edges : [];

    /// <summary>Every node reachable by following out-edges from <paramref name="from" />, excluding itself.</summary>
    public IReadOnlyCollection<string> Descendants(string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(from);
        while (pending.Count > 0)
        {
            foreach (var edge in OutboundEdges(pending.Pop()).Where(edge => seen.Add(edge.To)))
            {
                pending.Push(edge.To);
            }
        }

        _ = seen.Remove(from);
        return seen;
    }

    /// <summary>
    ///     One template's nodes: its root and everything reachable from it WITHOUT passing through the join.
    ///     <para>
    ///         The join is where a template subtree hands its work back to the graph, so it belongs to the graph and not
    ///         to the template — walking through it would swallow the whole rest of the run into the set of nodes a run
    ///         start refuses to instantiate.
    ///     </para>
    /// </summary>
    public HashSet<string> TemplateSubtree(DevWorkflowMaterialization materialization)
    {
        ArgumentNullException.ThrowIfNull(materialization);

        var subtree = new HashSet<string>(StringComparer.Ordinal)
        {
            materialization.TemplateNodeKey
        };
        var pending = new Stack<string>();
        pending.Push(materialization.TemplateNodeKey);
        while (pending.Count > 0)
        {
            foreach (var edge in OutboundEdges(pending.Pop())
                         .Where(edge => !string.Equals(edge.To, materialization.JoinNodeKey, StringComparison.Ordinal) && subtree.Add(edge.To)))
            {
                pending.Push(edge.To);
            }
        }

        return subtree;
    }

    /// <summary>
    ///     Parses the graph and enforces every structural rule. One method, because parsing IS the validation here: a
    ///     graph that survives this is one the dispatcher can route without a second opinion.
    ///     <para>
    ///         Today its only caller is the dispatcher's graph cache, so a bad graph is refused at RUN START. The
    ///         definition endpoints call it at save time too once they exist, which is where the same rules become an
    ///         author-time 400 rather than a failed run — and re-validating at run start stays necessary either way,
    ///         because an agent definition can be deleted between the save and the start.
    ///     </para>
    /// </summary>
    public static DevWorkflowGraph Parse(string graphJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphJson);

        using var document = ParseDocument(graphJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException("A workflow graph must be a JSON object.");
        }

        if (root.TryGetProperty("schemaVersion", out var schemaVersion)
            && (schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != SupportedSchemaVersion))
        {
            throw new DevWorkflowValidationException($"This node understands workflow graph schema version {SupportedSchemaVersion} only.");
        }

        var nodes = ParseNodes(root);
        var edges = ParseEdges(root, nodes);
        var graph = new DevWorkflowGraph(nodes, edges);
        graph.Validate();
        return graph;
    }

    private static JsonDocument ParseDocument(string graphJson)
    {
        try
        {
            return JsonDocument.Parse(graphJson);
        }
        catch (JsonException exception)
        {
            throw new DevWorkflowValidationException($"The workflow graph is not valid JSON: {exception.Message}");
        }
    }

    private static Dictionary<string, DevWorkflowGraphNode> ParseNodes(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new DevWorkflowValidationException("A workflow graph needs a 'nodes' array.");
        }

        var nodes = new Dictionary<string, DevWorkflowGraphNode>(StringComparer.Ordinal);
        foreach (var element in nodesElement.EnumerateArray())
        {
            var node = ParseNode(element);
            if (!nodes.TryAdd(node.NodeKey, node))
            {
                throw new DevWorkflowValidationException($"The workflow graph declares node key '{node.NodeKey}' twice.");
            }
        }

        if (nodes.Count == 0)
        {
            throw new DevWorkflowValidationException("A workflow graph needs at least one node.");
        }

        return nodes;
    }

    private static DevWorkflowGraphNode ParseNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException("Every entry of 'nodes' must be an object.");
        }

        var nodeKey = RequiredString(element, "nodeKey", "a node");
        var nodeType = RequiredEnum<DevWorkflowNodeType>(element, "nodeType", $"node '{nodeKey}'");
        var isWorkNode = nodeType is DevWorkflowNodeType.Agent or DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask;
        var commandIds = OptionalStringArray(element, "validationCommandIds", nodeKey);
        var toolMode = ParseToolMode(element, nodeKey, nodeType, commandIds);

        return new DevWorkflowGraphNode(nodeKey,
            nodeType,
            OptionalString(element, "label") ?? nodeKey,
            OptionalGuid(element, "agentDefinitionId", nodeKey),
            OptionalString(element, "agentSeedSlug"),
            OptionalString(element, "instructions"),
            commandIds,
            OptionalEnum(element, "joinPolicy", nodeKey, DevWorkflowJoinPolicy.All),
            OptionalPositiveInt(element, "maxAttempts", nodeKey) ?? (isWorkNode ? DefaultWorkNodeMaxAttempts : 1),
            OptionalNonNegativeInt(element, "retryDelaySeconds", nodeKey) ?? 0,
            OptionalPositiveInt(element, "nodeTimeoutSeconds", nodeKey),
            OptionalString(element, "retryTarget"),
            ParseMaterialization(element, nodeKey),
            toolMode);
    }

    /// <summary>
    ///     Which of the two things a Tool node is. Refused on any other node type even when it names the default: a
    ///     field that does nothing where it is written is a definition saying something the runtime will not do.
    /// </summary>
    private static DevWorkflowToolMode ParseToolMode(JsonElement element,
        string nodeKey,
        DevWorkflowNodeType nodeType,
        IReadOnlyList<string> commandIds)
    {
        if (element.TryGetProperty("toolMode", out var declared)
            && declared.ValueKind != JsonValueKind.Null
            && nodeType != DevWorkflowNodeType.Tool)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' declares a 'toolMode' but is a {nodeType} node, and only a Tool node runs one.");
        }

        var toolMode = OptionalEnum(element, "toolMode", nodeKey, DevWorkflowToolMode.Validate);
        if (toolMode == DevWorkflowToolMode.Apply && commandIds.Count > 0)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' applies approved patches and names validation commands, which it will never run. "
                                                     + "Validating the integrated result is a Tool node of its own, after this one.");
        }

        return toolMode;
    }

    private static DevWorkflowMaterialization? ParseMaterialization(JsonElement element, string nodeKey)
    {
        if (!element.TryGetProperty("materialization", out var materialization) || materialization.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (materialization.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException($"The 'materialization' on node '{nodeKey}' must be an object.");
        }

        return new DevWorkflowMaterialization(RequiredString(materialization, "templateNodeKey", $"the materialization on node '{nodeKey}'"),
            RequiredEnum<DevWorkflowArtifactKind>(materialization, "artifactKind", $"the materialization on node '{nodeKey}'"),
            RequiredString(materialization, "joinNodeKey", $"the materialization on node '{nodeKey}'"),
            OptionalPositiveInt(materialization, "maxChildren", nodeKey)
            ?? throw new DevWorkflowValidationException($"The materialization on node '{nodeKey}' needs a positive 'maxChildren'."));
    }

    private static List<DevWorkflowGraphEdge> ParseEdges(JsonElement root, Dictionary<string, DevWorkflowGraphNode> nodes)
    {
        var edges = new List<DevWorkflowGraphEdge>();
        var pairs = new HashSet<(string From, string To)>();
        if (!root.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind == JsonValueKind.Null)
        {
            return edges;
        }

        if (edgesElement.ValueKind != JsonValueKind.Array)
        {
            throw new DevWorkflowValidationException("The 'edges' member of a workflow graph must be an array.");
        }

        foreach (var element in edgesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new DevWorkflowValidationException("Every entry of 'edges' must be an object.");
            }

            var from = RequiredString(element, "from", "an edge");
            var to = RequiredString(element, "to", $"the edge out of '{from}'");
            var edge = new DevWorkflowGraphEdge(from,
                to,
                element.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
                    ? DevWorkflowCondition.Parse(condition, $"'{from}' → '{to}'")
                    : null);

            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to))
            {
                throw new DevWorkflowValidationException($"Edge {edge} names a node the graph does not declare.");
            }

            // One edge per pair of nodes. Two of them cannot mean what an author writing two would intend: the
            // admission rule judges every inbound edge on its own, so a second edge whose condition does not fire is
            // DEAD and skips the target — an "or" written this way routes the opposite of the way it reads. Refusing
            // it here is also what keeps the pair usable as a key, which the materialization's edge rewrite relies on.
            if (!pairs.Add((from, to)))
            {
                throw new DevWorkflowValidationException($"The workflow graph declares edge {edge} twice. A second edge between the same two nodes "
                                                         + "cannot widen the first: each is judged on its own, and one whose condition does not fire skips the target.");
            }

            edges.Add(edge);
        }

        return edges;
    }

    /// <summary>
    ///     The structural rules, all of which exist because breaking one produces a run that hangs rather than one that
    ///     fails: a cycle never terminates, an unreachable node never becomes eligible, a one-edge <c>Any</c> is an
    ///     <c>All</c> written confusingly, and a retry target that is not an ancestor is the cycle by another name.
    /// </summary>
    private void Validate()
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Materialization is { } materialization)
            {
                EnsureDeclared(materialization.TemplateNodeKey, $"The materialization on node '{node.NodeKey}' names template node");
                EnsureDeclared(materialization.JoinNodeKey, $"The materialization on node '{node.NodeKey}' names join node");
                if (materialization.MaxChildren > MaxTemplateChildren)
                {
                    throw new DevWorkflowValidationException($"The materialization on node '{node.NodeKey}' allows {materialization.MaxChildren} children, "
                                                             + $"more than the {MaxTemplateChildren} one decomposition may expand into.");
                }

                ValidateTemplateSubtree(node.NodeKey, materialization);
            }

            if (node.JoinPolicy == DevWorkflowJoinPolicy.Any && InboundEdges(node.NodeKey).Count < 2)
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares joinPolicy 'Any' with fewer than two inbound edges. "
                                                         + "One edge makes it an 'All' written confusingly, and none would never fire.");
            }
        }

        // Deliberately exempt: a template subtree has no inbound edge from outside on purpose, so that the editor can
        // author it and this validator can check it while nothing ever instantiates it directly.
        var entries = EntryNodeKeys.Where(key => !TemplateKeys.Contains(key)).ToList();
        if (entries.Count != 1)
        {
            throw new DevWorkflowValidationException(entries.Count == 0
                ? "A workflow graph needs exactly one entry node — one with no inbound edges — and this one has none."
                : $"A workflow graph needs exactly one entry node; this one has {entries.Count}: {string.Join(", ", entries)}.");
        }

        EnsureAcyclic();
        EnsureAppliesAreGated(entries[0]);

        // Template subtrees are exempt WHOLE: the edge rule above is what makes that safe, because it says the only way
        // out of one is the join, so exempting the set cannot exempt anything the run would otherwise have to run.
        var reachable = new HashSet<string>(Descendants(entries[0]), StringComparer.Ordinal)
        {
            entries[0]
        };
        reachable.UnionWith(TemplateKeys);

        if (Nodes.Keys.FirstOrDefault(key => !reachable.Contains(key)) is { } orphan)
        {
            throw new DevWorkflowValidationException($"Node '{orphan}' is unreachable from the entry node, so nothing would ever run it.");
        }

        foreach (var node in Nodes.Values.Where(static node => node.RetryTarget is not null))
        {
            EnsureDeclared(node.RetryTarget!, $"Node '{node.NodeKey}' declares retryTarget");
            if (!Descendants(node.RetryTarget!).Contains(node.NodeKey, StringComparer.Ordinal))
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares retryTarget '{node.RetryTarget}', which is not one of its ancestors. "
                                                         + "Routing a failure to a node that does not lead back here would livelock the run.");
            }
        }
    }

    /// <summary>
    ///     Y3 made structural: no path from the entry node to an apply node may avoid a human gate.
    ///     <para>
    ///         The rule the whole integration stage rests on is that no AI-authored patch reaches a real repository
    ///         without an operator decision recorded in the run's own audit trail. A template that placed the gate
    ///         elsewhere — or nowhere — would be a definition that quietly applies, and nothing downstream could tell
    ///         the difference: by the time the node runs, the decision it should have waited for does not exist to be
    ///         missed. Checking it here costs one walk and makes the invariant a property of the graph.
    ///     </para>
    ///     <para>
    ///         The walk is the dominance question stated the cheap way: follow the edges from the entry but never THROUGH
    ///         a human gate, and whatever is still reachable is reachable without an approval. An apply node inside a
    ///         materialization template is not reachable from the entry at all, so it is refused above rather than here.
    ///     </para>
    /// </summary>
    private void EnsureAppliesAreGated(string entry)
    {
        var applies = Nodes.Values.Where(static node => node.ToolMode == DevWorkflowToolMode.Apply).Select(static node => node.NodeKey).ToList();
        if (applies.Count == 0)
        {
            return;
        }

        if (applies.FirstOrDefault(TemplateKeys.Contains) is { } cloned)
        {
            throw new DevWorkflowValidationException($"Node '{cloned}' applies approved patches and is inside a materialization template, so every child would apply "
                                                     + "the whole fan-out again. Integration runs once, after the join.");
        }

        var ungated = new HashSet<string>(StringComparer.Ordinal)
        {
            entry
        };
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.Count > 0)
        {
            var nodeKey = pending.Pop();
            if (Nodes[nodeKey].NodeType == DevWorkflowNodeType.HumanGate)
            {
                continue;
            }

            foreach (var edge in OutboundEdges(nodeKey).Where(edge => ungated.Add(edge.To)))
            {
                pending.Push(edge.To);
            }
        }

        if (applies.FirstOrDefault(ungated.Contains) is { } exposed)
        {
            throw new DevWorkflowValidationException($"Node '{exposed}' applies approved patches on a path from the entry node that passes no human gate. An apply "
                                                     + "changes a real repository, so a definition has to put an approval in front of it.");
        }
    }

    /// <summary>
    ///     The structural rule on a template SUBTREE: nothing outside it points into it.
    ///     <para>
    ///         It exists because breaking it HANGS rather than fails — a node outside the template that depended on it
    ///         would wait, at run start and forever, on the one node deliberately never instantiated. It is also the
    ///         whole of the rule the plan states in two halves: because the subtree is defined as everything the
    ///         template reaches SHORT OF the join, "no edge leaves it except to the join" is true by construction, and
    ///         the only way a live node could be swallowed into a template is by being pointed at from outside it,
    ///         which is what this refuses.
    ///     </para>
    /// </summary>
    private void ValidateTemplateSubtree(string nodeKey, DevWorkflowMaterialization materialization)
    {
        var subtree = TemplateSubtree(materialization);
        foreach (var key in subtree)
        {
            if (Nodes[key].Materialization is not null && !string.Equals(key, nodeKey, StringComparison.Ordinal))
            {
                throw new DevWorkflowValidationException($"Node '{key}' decomposes work and is inside the materialization template of node '{nodeKey}'. Nested "
                                                         + "materialization is not supported: a clone that decomposed again would expand a template already expanded.");
            }

            if (InboundEdges(key).FirstOrDefault(edge => !subtree.Contains(edge.From)) is { } inbound)
            {
                throw new DevWorkflowValidationException($"Edge {inbound} points into the materialization template of node '{nodeKey}' from outside it. A template "
                                                         + "subtree is cloned once per task, so nothing outside it may depend on the copy that is never run.");
            }
        }
    }

    /// <summary>Depth-first colouring: white unvisited, grey on the current path, black finished. A grey hit is the cycle.</summary>
    private void EnsureAcyclic()
    {
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeKey in Nodes.Keys)
        {
            Walk(nodeKey);
        }

        void Walk(string nodeKey)
        {
            if (finished.Contains(nodeKey))
            {
                return;
            }

            if (!onPath.Add(nodeKey))
            {
                throw new DevWorkflowValidationException($"The workflow graph has a cycle through node '{nodeKey}'. A fix loop is a retryTarget, not a back edge.");
            }

            foreach (var edge in OutboundEdges(nodeKey))
            {
                Walk(edge.To);
            }

            _ = onPath.Remove(nodeKey);
            _ = finished.Add(nodeKey);
        }
    }

    private void EnsureDeclared(string nodeKey, string description)
    {
        if (!Nodes.ContainsKey(nodeKey))
        {
            throw new DevWorkflowValidationException($"{description} '{nodeKey}', which the graph does not declare.");
        }
    }

    private static string RequiredString(JsonElement element, string name, string owner)
    {
        var value = OptionalString(element, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new DevWorkflowValidationException($"{char.ToUpperInvariant(owner[0])}{owner[1..]} needs a non-empty '{name}'.")
            : value;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TEnum RequiredEnum<TEnum>(JsonElement element, string name, string owner)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(OptionalString(element, name), ignoreCase: true, out var parsed)
            ? parsed
            : throw new DevWorkflowValidationException($"{char.ToUpperInvariant(owner[0])}{owner[1..]} needs a '{name}' from {string.Join(", ", Enum.GetNames<TEnum>())}.");

    private static TEnum OptionalEnum<TEnum>(JsonElement element, string name, string nodeKey, TEnum fallback)
        where TEnum : struct, Enum
    {
        var raw = OptionalString(element, name);
        if (raw is null)
        {
            return fallback;
        }

        return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : throw new DevWorkflowValidationException($"Node '{nodeKey}' has an unknown '{name}' of '{raw}'; expected one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static Guid? OptionalGuid(JsonElement element, string name, string nodeKey)
    {
        var raw = OptionalString(element, name);
        if (raw is null)
        {
            return null;
        }

        return Guid.TryParse(raw, out var parsed)
            ? parsed
            : throw new DevWorkflowValidationException($"Node '{nodeKey}' has a '{name}' of '{raw}', which is not a GUID.");
    }

    private static IReadOnlyList<string> OptionalStringArray(JsonElement element, string name, string nodeKey)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(static entry => entry.ValueKind != JsonValueKind.String))
        {
            throw new DevWorkflowValidationException($"The '{name}' on node '{nodeKey}' must be an array of strings.");
        }

        return [.. array.EnumerateArray().Select(static entry => entry.GetString()!)];
    }

    private static int? OptionalPositiveInt(JsonElement element, string name, string nodeKey) =>
        OptionalBoundedInt(element, name, nodeKey, minimum: 1, "must be positive");

    private static int? OptionalNonNegativeInt(JsonElement element, string name, string nodeKey) =>
        OptionalBoundedInt(element, name, nodeKey, minimum: 0, "cannot be negative");

    private static int? OptionalBoundedInt(JsonElement element, string name, string nodeKey, int minimum, string complaint)
    {
        if (OptionalInt(element, name, nodeKey) is not { } value)
        {
            return null;
        }

        return value >= minimum
            ? value
            : throw new DevWorkflowValidationException($"The '{name}' on node '{nodeKey}' {complaint}.");
    }

    private static int? OptionalInt(JsonElement element, string name, string nodeKey)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new DevWorkflowValidationException($"The '{name}' on node '{nodeKey}' must be a whole number.");
    }
}
