namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>How a node with more than one inbound edge waits. The whole of the join semantics.</summary>
internal enum DevWorkflowJoinPolicy
{
    All,
    Any
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
    DevWorkflowMaterialization? Materialization);

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
    }

    public IReadOnlyDictionary<string, DevWorkflowGraphNode> Nodes { get; }

    public IReadOnlyList<DevWorkflowGraphEdge> Edges { get; }

    /// <summary>
    ///     Nodes with no inbound edge. Start is implicit, so this is what "entry node" means — and it is also why a
    ///     materialization template node, which is deliberately unreachable, must be excluded before this is read as the
    ///     set to materialize at run start.
    /// </summary>
    public IReadOnlyList<string> EntryNodeKeys { get; }

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

        return new DevWorkflowGraphNode(nodeKey,
            nodeType,
            OptionalString(element, "label") ?? nodeKey,
            OptionalGuid(element, "agentDefinitionId", nodeKey),
            OptionalString(element, "agentSeedSlug"),
            OptionalString(element, "instructions"),
            OptionalStringArray(element, "validationCommandIds", nodeKey),
            OptionalEnum(element, "joinPolicy", nodeKey, DevWorkflowJoinPolicy.All),
            OptionalPositiveInt(element, "maxAttempts", nodeKey) ?? (isWorkNode ? DefaultWorkNodeMaxAttempts : 1),
            OptionalNonNegativeInt(element, "retryDelaySeconds", nodeKey) ?? 0,
            OptionalPositiveInt(element, "nodeTimeoutSeconds", nodeKey),
            OptionalString(element, "retryTarget"),
            ParseMaterialization(element, nodeKey));
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
        var templateKeys = Nodes.Values.Where(static node => node.Materialization is not null)
                                .Select(static node => node.Materialization!.TemplateNodeKey)
                                .ToHashSet(StringComparer.Ordinal);

        foreach (var node in Nodes.Values)
        {
            if (node.Materialization is { } materialization)
            {
                EnsureDeclared(materialization.TemplateNodeKey, $"The materialization on node '{node.NodeKey}' names template node");
                EnsureDeclared(materialization.JoinNodeKey, $"The materialization on node '{node.NodeKey}' names join node");

                // A template is cloned once per task and every edge its children get is synthesised at materialization,
                // so it carries none of its own. The rule exists because the alternative HANGS rather than fails: a
                // template's declared successor would be given a node run at run start, and then wait forever on an
                // inbound edge whose source is the one node deliberately never instantiated.
                if (InboundEdges(materialization.TemplateNodeKey).Count > 0 || OutboundEdges(materialization.TemplateNodeKey).Count > 0)
                {
                    throw new DevWorkflowValidationException($"Template node '{materialization.TemplateNodeKey}' declares edges of its own. A materialization "
                                                             + "template is cloned once per task and its children's edges are generated, so the template itself must have none.");
                }
            }

            if (node.JoinPolicy == DevWorkflowJoinPolicy.Any && InboundEdges(node.NodeKey).Count < 2)
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares joinPolicy 'Any' with fewer than two inbound edges. "
                                                         + "One edge makes it an 'All' written confusingly, and none would never fire.");
            }
        }

        // Deliberately exempt: a template node has no inbound edge on purpose, so that the editor can author it and this
        // validator can check it while nothing ever instantiates it directly. C2 widens this to a template SUBTREE —
        // its root plus descendants, cloned whole per task — at which point the edge rule above widens with it.
        var entries = EntryNodeKeys.Where(key => !templateKeys.Contains(key)).ToList();
        if (entries.Count != 1)
        {
            throw new DevWorkflowValidationException(entries.Count == 0
                ? "A workflow graph needs exactly one entry node — one with no inbound edges — and this one has none."
                : $"A workflow graph needs exactly one entry node; this one has {entries.Count}: {string.Join(", ", entries)}.");
        }

        EnsureAcyclic();

        // Templates are exempt, and by the edge rule above they are exempt alone: a template has no subtree to carry in.
        var reachable = new HashSet<string>(Descendants(entries[0]), StringComparer.Ordinal)
        {
            entries[0]
        };
        reachable.UnionWith(templateKeys);

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
