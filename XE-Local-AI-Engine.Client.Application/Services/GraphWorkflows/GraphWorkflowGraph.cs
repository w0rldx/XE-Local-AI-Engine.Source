namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>Where the editor drew a node. Authoring metadata the runtime never reads.</summary>
internal sealed record GraphWorkflowPosition(double X, double Y);

/// <summary>
///     The per-kind settings of one node. Discriminated rather than a bag, because a member that does nothing where it
///     is written is a definition saying something the runtime will not do — so the parser refuses it there.
/// </summary>
internal abstract record GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowStartConfig(JsonElement? InputSchema, JsonElement? DefaultInput) : GraphWorkflowNodeConfig;

/// <summary>
///     <see cref="Model" /> and <see cref="ReasoningEffort" /> are the two dispatch pins. The model name travels as
///     written and is matched against this node's catalog when the run starts, exactly as an agent definition's own pin
///     is, so a graph does not become unsaveable because a model was uninstalled after it was authored. The effort IS
///     checked here: its vocabulary is closed and cannot go stale between authoring and a run.
/// </summary>
internal sealed record GraphWorkflowAgentConfig(
    Guid? AgentDefinitionId,
    string Instructions,
    string? Model,
    string? ReasoningEffort,
    JsonElement? ResponseJsonSchema,
    bool IncludeUpstreamOutputs) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowToolConfig(string ToolName, JsonElement? Arguments, IReadOnlyDictionary<string, string> ArgumentBindings) : GraphWorkflowNodeConfig;

/// <summary>
///     <see cref="Path" /> is the node-level DEFAULT dot path its own out-edges inherit when their condition omits one.
///     Optional: an author may write the path on every branch instead.
/// </summary>
internal sealed record GraphWorkflowConditionConfig(string? Path) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowPauseConfig(string Prompt, IReadOnlyList<GraphWorkflowDecisionKind> AllowedDecisions, bool RequireComment) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowEndConfig(string Outcome, string? ResultPath) : GraphWorkflowNodeConfig;

/// <summary>The config of a kind that has none — <c>Parallel</c> and <c>Join</c> are shape, not settings.</summary>
internal sealed record GraphWorkflowEmptyConfig : GraphWorkflowNodeConfig;

/// <summary>
///     One node of the parsed graph. Only what the runtime reads: unknown properties survive in the stored blob either
///     way — this projection is not a re-serialization of it.
/// </summary>
internal sealed record GraphWorkflowGraphNode(
    string NodeKey,
    GraphWorkflowNodeKind Kind,
    string Label,
    GraphWorkflowJoinPolicy JoinPolicy,
    int MaxAttempts,
    int? TimeoutSeconds,
    GraphWorkflowPosition? Position,
    GraphWorkflowNodeConfig Config);

/// <summary>
///     One edge. <see cref="Key" /> is its identity — required and unique, which is what makes PARALLEL edges
///     expressible: two edges over the same pair are legal when at most one of them is unconditional.
///     <see cref="Label" /> is the named outcome a node's output document reports as its <c>branch</c>.
/// </summary>
internal sealed record GraphWorkflowGraphEdge(string Key, string From, string To, string? Label, GraphWorkflowCondition? Condition)
{
    public override string ToString() =>
        $"'{Key}' ('{From}' → '{To}')";
}

/// <summary>
///     The parsed, in-memory projection of a definition's or a run's pinned <c>graph_json</c> — the single source of
///     routing truth.
///     <para>
///         <see cref="Parse" /> is the only entry point and parsing IS the validation: a graph that survives it is one
///         the dispatcher can route without a second opinion. Save time and run start share it, so a graph accepted at
///         save is one that will start.
///     </para>
/// </summary>
internal sealed class GraphWorkflowGraph
{
    private const int SupportedSchemaVersion = 1;

    /// <summary>The sentence every dot-path refusal ends with, so an author reads the same rule wherever they hit it.</summary>
    private const string DotPathRule = "A dot path is property names separated by '.', with no wildcards, indexes or functions.";

    /// <summary>Defaults for a node that names none. Waits and inline decisions get one try; work gets three.</summary>
    private const int DefaultWorkNodeMaxAttempts = 3;

    /// <summary>
    ///     Node and edge keys share ONE namespace and this charset. A key reaches a plaintext <c>node_key</c> column, a
    ///     React Flow element id, a URL search param and a terminal-reason sentence, and the charset is what keeps all
    ///     four honest at once.
    /// </summary>
    private const int MaxKeyLength = 64;

    /// <summary>
    ///     The reasoning efforts an Agent node may name, which are the ones an agent definition may pin — the override
    ///     has to be sayable in the same vocabulary as the pin it replaces. Not an enum: this travels to the provider as
    ///     the string it is written as.
    /// </summary>
    private static readonly string[] ReasoningEfforts = ["none", "low", "medium", "high"];

    /// <summary>
    ///     The config members each kind reads, and the whole of them. Anything else on a node's config is refused, which
    ///     is how a Tool node's <c>toolName</c> written on an Agent node becomes an author-time error rather than a
    ///     setting nothing applies.
    /// </summary>
    private static readonly Dictionary<GraphWorkflowNodeKind, string[]> ConfigMembers = new()
    {
        [GraphWorkflowNodeKind.Start] = ["inputSchema", "defaultInput"],
        [GraphWorkflowNodeKind.Agent] = ["agentDefinitionId", "instructions", "model", "reasoningEffort", "responseJsonSchema", "includeUpstreamOutputs"],
        [GraphWorkflowNodeKind.Tool] = ["toolName", "arguments", "argumentBindings"],
        [GraphWorkflowNodeKind.Condition] = ["path"],
        [GraphWorkflowNodeKind.Parallel] = [],
        [GraphWorkflowNodeKind.Join] = [],
        [GraphWorkflowNodeKind.Pause] = ["prompt", "allowedDecisions", "requireComment"],
        [GraphWorkflowNodeKind.End] = ["outcome", "resultPath"]
    };

    /// <summary>Detached from its document by <c>Clone</c>, so a node that declares no config reads as an empty one.</summary>
    private static readonly JsonElement EmptyConfig = CloneEmptyObject();

    private static readonly Dictionary<string, string> NoArgumentBindings = new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<GraphWorkflowGraphEdge>> _inbound;
    private readonly Dictionary<string, List<GraphWorkflowGraphEdge>> _outbound;

    private GraphWorkflowGraph(IReadOnlyDictionary<string, GraphWorkflowGraphNode> nodes, IReadOnlyList<GraphWorkflowGraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _inbound = nodes.Keys.ToDictionary(key => key, _ => new List<GraphWorkflowGraphEdge>(), StringComparer.Ordinal);
        _outbound = nodes.Keys.ToDictionary(key => key, _ => new List<GraphWorkflowGraphEdge>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            _outbound[edge.From].Add(edge);
            _inbound[edge.To].Add(edge);
        }

        EntryNodeKeys = [.. nodes.Keys.Where(key => _inbound[key].Count == 0).OrderBy(key => key, StringComparer.Ordinal)];
        TerminalNodeKeys = new HashSet<string>(nodes.Keys.Where(key => _outbound[key].Count == 0), StringComparer.Ordinal);
        ToolNodeNames =
        [
            .. nodes.Values.Where(static node => node.Config is GraphWorkflowToolConfig)
                    .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                    .Select(static node => ((GraphWorkflowToolConfig)node.Config).ToolName)
                    .Distinct(StringComparer.Ordinal)
        ];
    }

    public IReadOnlyDictionary<string, GraphWorkflowGraphNode> Nodes { get; }

    public IReadOnlyList<GraphWorkflowGraphEdge> Edges { get; }

    /// <summary>
    ///     Nodes with no inbound edge. Under the rules below this is exactly the one <c>Start</c> node — the validator
    ///     refuses a second entry through the reachability rule and an inbound edge into <c>Start</c> outright.
    /// </summary>
    public IReadOnlyList<string> EntryNodeKeys { get; }

    /// <summary>
    ///     Nodes no edge leaves — what "the run got somewhere" means. A run is <c>Completed</c> only once one of these
    ///     SUCCEEDED, so a tail that was skipped cannot read as the run having done its job.
    ///     <para>
    ///         Under the <c>End</c> rules this set and the <c>End</c> nodes coincide by construction, which is why the
    ///         state machine keeps reading this rather than the kind.
    ///     </para>
    /// </summary>
    public IReadOnlySet<string> TerminalNodeKeys { get; }

    /// <summary>
    ///     Every distinct tool name a <c>Tool</c> node in this graph would run, in node-key order. The parser cannot
    ///     reach the tool catalog, so this is the seam the save-time and run-start tool gates ask over.
    /// </summary>
    public IReadOnlyList<string> ToolNodeNames { get; }

    public IReadOnlyList<GraphWorkflowGraphEdge> InboundEdges(string nodeKey) =>
        _inbound.TryGetValue(nodeKey, out var edges) ? edges : [];

    public IReadOnlyList<GraphWorkflowGraphEdge> OutboundEdges(string nodeKey) =>
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
    ///     Every node that reaches <paramref name="to" /> by following out-edges, excluding itself — the mirror of
    ///     <see cref="Descendants" />, over the inbound index. This is what "upstream of" means on a graph with more
    ///     than one branch: a node on a PARALLEL branch is neither an ancestor nor a descendant.
    /// </summary>
    public IReadOnlySet<string> Ancestors(string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(to);
        while (pending.Count > 0)
        {
            foreach (var edge in InboundEdges(pending.Pop()).Where(edge => seen.Add(edge.From)))
            {
                pending.Push(edge.From);
            }
        }

        _ = seen.Remove(to);
        return seen;
    }

    /// <summary>
    ///     Parses the graph and enforces every rule. Two kinds of failure, on purpose: a malformed document and the
    ///     whole-graph structural rules THROW immediately, because there is nothing useful to say about the rest of a
    ///     graph nobody can walk; every per-node and per-edge failure ACCUMULATES, keyed by the element it belongs to,
    ///     so an author fixing a canvas gets every complaint at once.
    ///     <para>
    ///         The node cap is deliberately NOT enforced here — it is an option, and this stays testable without a
    ///         container. <c>GraphWorkflowGraphContract.ValidateAndCountNodes</c> checks it after the parse.
    ///     </para>
    /// </summary>
    public static GraphWorkflowGraph Parse(string graphJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphJson);

        using var document = ParseDocument(graphJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GraphWorkflowValidationException("A graph workflow definition must be a JSON object.");
        }

        if (root.TryGetProperty("schemaVersion", out var schemaVersion)
            && (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version) || version != SupportedSchemaVersion))
        {
            throw new GraphWorkflowValidationException($"This node understands graph workflow schema version {SupportedSchemaVersion} only.");
        }

        var errors = new List<GraphWorkflowValidationError>();

        // ONE namespace for node and edge keys: an edge key colliding with a node key makes an element lookup
        // ambiguous in the editor for no gain.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var nodes = ParseNodes(root, keys, errors);
        var edges = ParseEdges(root, nodes, keys, errors);
        var graph = new GraphWorkflowGraph(nodes, edges);
        graph.Validate(errors);
        if (errors.Count > 0)
        {
            throw new GraphWorkflowValidationException(GraphWorkflowValidationResult.Invalid(errors));
        }

        return graph;
    }

    /// <summary>
    ///     An empty JSON object, detached from the document that produced it — which is then disposed. A clone owns its
    ///     own bytes, so the document has no reader left to keep alive.
    /// </summary>
    private static JsonElement CloneEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonDocument ParseDocument(string graphJson)
    {
        try
        {
            return JsonDocument.Parse(graphJson);
        }
        catch (JsonException exception)
        {
            throw new GraphWorkflowValidationException($"The graph workflow definition is not valid JSON: {exception.Message}");
        }
    }

    /// <summary>
    ///     Runs one per-element parse and turns its refusal into an accumulated error rather than a throw. The scalar
    ///     helpers stay exactly as they read — one place decides which failures are worth collecting.
    /// </summary>
    private static T Collect<T>(List<GraphWorkflowValidationError> errors, string key, Func<T> parse, T fallback)
    {
        try
        {
            return parse();
        }
        catch (GraphWorkflowValidationException exception)
        {
            errors.Add(new GraphWorkflowValidationError(key, exception.Message));
            return fallback;
        }
    }

    private static Dictionary<string, GraphWorkflowGraphNode> ParseNodes(JsonElement root,
        HashSet<string> keys,
        List<GraphWorkflowValidationError> errors)
    {
        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs a 'nodes' array.");
        }

        var nodes = new Dictionary<string, GraphWorkflowGraphNode>(StringComparer.Ordinal);
        foreach (var element in nodesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new GraphWorkflowValidationException("Every entry of 'nodes' must be an object.");
            }

            // Key and kind are structural: they are this node's identity and its discriminator, and nothing else about
            // it can be read without them.
            var nodeKey = RequiredKey(element, "key", "a node");
            if (!keys.Add(nodeKey))
            {
                throw new GraphWorkflowValidationException($"The graph workflow definition declares key '{nodeKey}' twice. "
                                                           + "Node and edge keys share one namespace.");
            }

            var kind = RequiredEnum<GraphWorkflowNodeKind>(element, "kind", $"node '{nodeKey}'");
            nodes[nodeKey] = ParseNode(element, nodeKey, kind, errors);
        }

        if (nodes.Count == 0)
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs at least one node.");
        }

        return nodes;
    }

    private static GraphWorkflowGraphNode ParseNode(JsonElement element,
        string nodeKey,
        GraphWorkflowNodeKind kind,
        List<GraphWorkflowValidationError> errors)
    {
        var isWorkNode = kind is GraphWorkflowNodeKind.Agent or GraphWorkflowNodeKind.Tool;
        return new GraphWorkflowGraphNode(nodeKey,
            kind,
            OptionalString(element, "label") ?? nodeKey,
            Collect(errors, nodeKey, () => OptionalEnum(element, "joinPolicy", nodeKey, GraphWorkflowJoinPolicy.All), GraphWorkflowJoinPolicy.All),
            Collect(errors, nodeKey, () => OptionalPositiveInt(element, "maxAttempts", nodeKey), null) ?? (isWorkNode ? DefaultWorkNodeMaxAttempts : 1),
            Collect(errors, nodeKey, () => OptionalPositiveInt(element, "timeoutSeconds", nodeKey), null),
            Collect(errors, nodeKey, () => ParsePosition(element, nodeKey), null),
            Collect<GraphWorkflowNodeConfig>(errors, nodeKey, () => ParseConfig(element, nodeKey, kind), new GraphWorkflowEmptyConfig()));
    }

    /// <summary>
    ///     Optional, and shape-checked when present, so a malformed one is refused at save rather than at draw time. A
    ///     node without a position is laid out client-side when the definition is opened.
    /// </summary>
    private static GraphWorkflowPosition? ParsePosition(JsonElement element, string nodeKey)
    {
        if (!element.TryGetProperty("position", out var position) || position.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (position.ValueKind != JsonValueKind.Object
            || !position.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var xValue)
            || !position.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetDouble(out var yValue))
        {
            throw new GraphWorkflowValidationException($"The 'position' on node '{nodeKey}' must be an object carrying a numeric 'x' and 'y'.");
        }

        return new GraphWorkflowPosition(xValue, yValue);
    }

    private static GraphWorkflowNodeConfig ParseConfig(JsonElement element, string nodeKey, GraphWorkflowNodeKind kind)
    {
        var config = EmptyConfig;
        if (element.TryGetProperty("config", out var declared) && declared.ValueKind != JsonValueKind.Null)
        {
            if (declared.ValueKind != JsonValueKind.Object)
            {
                throw new GraphWorkflowValidationException($"The 'config' on node '{nodeKey}' must be an object.");
            }

            config = declared;
        }

        var stray = config.EnumerateObject()
                          .Select(static member => member.Name)
                          .FirstOrDefault(member => !ConfigMembers[kind].Contains(member, StringComparer.Ordinal));
        if (stray is not null)
        {
            throw new GraphWorkflowValidationException($"Node '{nodeKey}' is a {kind} node and its config declares '{stray}', "
                                                       + $"which no {kind} node reads.");
        }

        var owner = $"the config on node '{nodeKey}'";
        return kind switch
        {
            GraphWorkflowNodeKind.Start => new GraphWorkflowStartConfig(OptionalElement(config, "inputSchema"), OptionalElement(config, "defaultInput")),
            GraphWorkflowNodeKind.Agent => new GraphWorkflowAgentConfig(OptionalGuid(config, "agentDefinitionId", nodeKey),
                RequiredString(config, "instructions", owner),
                TrimmedOptionalString(config, "model"),
                ParseReasoningEffort(config, nodeKey),
                ParseResponseJsonSchema(config, nodeKey),
                OptionalBool(config, "includeUpstreamOutputs", nodeKey, fallback: true)),
            GraphWorkflowNodeKind.Tool => new GraphWorkflowToolConfig(RequiredString(config, "toolName", owner),
                OptionalObject(config, "arguments", nodeKey),
                ParseArgumentBindings(config, nodeKey)),
            GraphWorkflowNodeKind.Condition => new GraphWorkflowConditionConfig(ParseDotPath(config, "path", nodeKey)),
            GraphWorkflowNodeKind.Pause => new GraphWorkflowPauseConfig(RequiredString(config, "prompt", owner),
                ParseAllowedDecisions(config, nodeKey),
                OptionalBool(config, "requireComment", nodeKey, fallback: false)),
            GraphWorkflowNodeKind.End => new GraphWorkflowEndConfig(RequiredString(config, "outcome", owner), ParseDotPath(config, "resultPath", nodeKey)),
            _ => new GraphWorkflowEmptyConfig()
        };
    }

    /// <summary>
    ///     The node's reasoning-effort override, checked against the four the agent surface itself accepts. An unknown
    ///     token is refused here rather than dropped at dispatch: unlike a model name, this vocabulary is closed and
    ///     cannot go stale between authoring and a run.
    /// </summary>
    private static string? ParseReasoningEffort(JsonElement config, string nodeKey)
    {
        var effort = TrimmedOptionalString(config, "reasoningEffort");
        if (effort is null || ReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            return effort;
        }

        throw new GraphWorkflowValidationException($"Node '{nodeKey}' has an unknown 'reasoningEffort' of '{effort}'; "
                                                   + $"expected one of {string.Join(", ", ReasoningEfforts)}.");
    }

    /// <summary>
    ///     The schema an Agent node's answer is held to. It must be an OBJECT schema: the output document carries the
    ///     parsed answer as <c>output.json</c>, and a condition reads a property off it.
    /// </summary>
    private static JsonElement? ParseResponseJsonSchema(JsonElement config, string nodeKey)
    {
        if (OptionalObject(config, "responseJsonSchema", nodeKey) is not { } schema)
        {
            return null;
        }

        var type = schema.TryGetProperty("type", out var declared) && declared.ValueKind == JsonValueKind.String ? declared.GetString() : null;
        return string.Equals(type, "object", StringComparison.Ordinal)
            ? schema
            : throw new GraphWorkflowValidationException($"The 'responseJsonSchema' on node '{nodeKey}' must be an object schema — its 'type' must be \"object\".");
    }

    private static IReadOnlyDictionary<string, string> ParseArgumentBindings(JsonElement config, string nodeKey)
    {
        if (!config.TryGetProperty("argumentBindings", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return NoArgumentBindings;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GraphWorkflowValidationException($"The 'argumentBindings' on node '{nodeKey}' must be an object mapping each argument to a dot path.");
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.String || !GraphWorkflowTokens.IsDotPath(member.Value.GetString()?.Trim()))
            {
                throw new GraphWorkflowValidationException($"The 'argumentBindings' on node '{nodeKey}' binds '{member.Name}' to something that is not a dot path. {DotPathRule}");
            }

            bindings[member.Name] = member.Value.GetString()!.Trim();
        }

        return bindings;
    }

    /// <summary>
    ///     The answers this pause accepts, as a non-empty distinct subset of the decision vocabulary. Empty would be a
    ///     question nobody could answer, and the run would wait on it forever.
    /// </summary>
    private static IReadOnlyList<GraphWorkflowDecisionKind> ParseAllowedDecisions(JsonElement config, string nodeKey)
    {
        if (!config.TryGetProperty("allowedDecisions", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new GraphWorkflowValidationException($"Node '{nodeKey}' is a Pause node and needs an 'allowedDecisions' array in its config.");
        }

        var decisions = new List<GraphWorkflowDecisionKind>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || !GraphWorkflowTokens.TryParseName<GraphWorkflowDecisionKind>(entry.GetString(), out var decision))
            {
                throw new GraphWorkflowValidationException($"Node '{nodeKey}' names a decision this runtime does not offer; "
                                                           + $"expected values from {string.Join(", ", Enum.GetNames<GraphWorkflowDecisionKind>())}.");
            }

            if (!decisions.Contains(decision))
            {
                decisions.Add(decision);
            }
        }

        return decisions.Count > 0
            ? decisions
            : throw new GraphWorkflowValidationException($"Node '{nodeKey}' is a Pause node and names no decisions, so nobody could ever answer it.");
    }

    private static List<GraphWorkflowGraphEdge> ParseEdges(JsonElement root,
        Dictionary<string, GraphWorkflowGraphNode> nodes,
        HashSet<string> keys,
        List<GraphWorkflowValidationError> errors)
    {
        var edges = new List<GraphWorkflowGraphEdge>();
        if (!root.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind == JsonValueKind.Null)
        {
            return edges;
        }

        if (edgesElement.ValueKind != JsonValueKind.Array)
        {
            throw new GraphWorkflowValidationException("The 'edges' member of a graph workflow definition must be an array.");
        }

        foreach (var element in edgesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new GraphWorkflowValidationException("Every entry of 'edges' must be an object.");
            }

            var edgeKey = RequiredKey(element, "key", "an edge");
            if (!keys.Add(edgeKey))
            {
                throw new GraphWorkflowValidationException($"The graph workflow definition declares key '{edgeKey}' twice. "
                                                           + "Node and edge keys share one namespace.");
            }

            var from = RequiredString(element, "from", $"edge '{edgeKey}'");
            var to = RequiredString(element, "to", $"edge '{edgeKey}'");

            // Structural, and deliberately not accumulated: the inbound and outbound indexes are built on these two, so
            // collecting past an endpoint the graph does not declare walks an adjacency that is already wrong and every
            // later complaint is noise.
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to))
            {
                throw new GraphWorkflowValidationException($"Edge '{edgeKey}' ('{from}' → '{to}') names a node the graph does not declare.");
            }

            edges.Add(new GraphWorkflowGraphEdge(edgeKey,
                from,
                to,
                TrimmedOptionalString(element, "label"),
                Collect(errors, edgeKey, () => ParseEdgeCondition(element, edgeKey, from, to, nodes), null)));
        }

        return edges;
    }

    /// <summary>
    ///     One edge's condition, taking its source <c>Condition</c> node's <c>config.path</c> when the edge omits one.
    ///     An edge's <c>sourceHandle</c> is read past and never stored: it is authoring metadata, like a position.
    /// </summary>
    private static GraphWorkflowCondition? ParseEdgeCondition(JsonElement element,
        string edgeKey,
        string from,
        string to,
        Dictionary<string, GraphWorkflowGraphNode> nodes)
    {
        if (!element.TryGetProperty("condition", out var condition) || condition.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var defaultPath = nodes[from] is { Kind: GraphWorkflowNodeKind.Condition, Config: GraphWorkflowConditionConfig { Path: { } path } } ? path : null;
        return GraphWorkflowCondition.Parse(condition, $"'{edgeKey}' ('{from}' → '{to}')", defaultPath);
    }

    /// <summary>
    ///     The rules, in two halves. The structural ones throw as they are found — a graph with no <c>Start</c>, a
    ///     cycle or an unreachable node is one nothing can walk, so there is nothing useful to say about the rest of
    ///     it. Everything after them accumulates against the node or edge it belongs to.
    /// </summary>
    private void Validate(List<GraphWorkflowValidationError> errors)
    {
        var starts = Nodes.Values.Where(static node => node.Kind == GraphWorkflowNodeKind.Start)
                          .Select(static node => node.NodeKey)
                          .Order(StringComparer.Ordinal)
                          .ToList();
        if (starts.Count != 1)
        {
            throw new GraphWorkflowValidationException(starts.Count == 0
                ? "A graph workflow definition needs exactly one Start node, and this one has none."
                : $"A graph workflow definition needs exactly one Start node; this one has {starts.Count}: {string.Join(", ", starts)}.");
        }

        var start = starts[0];
        if (InboundEdges(start).Count > 0)
        {
            throw new GraphWorkflowValidationException($"Node '{start}' is the Start node and something routes into it. "
                                                       + "A run begins there, so nothing can come before it.");
        }

        if (!Nodes.Values.Any(static node => node.Kind == GraphWorkflowNodeKind.End))
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs at least one End node, or no run could ever complete.");
        }

        EnsureAcyclic();

        var reachable = new HashSet<string>(Descendants(start), StringComparer.Ordinal)
        {
            start
        };
        if (Nodes.Keys.Where(key => !reachable.Contains(key)).Order(StringComparer.Ordinal).FirstOrDefault() is { } orphan)
        {
            throw new GraphWorkflowValidationException($"Node '{orphan}' is unreachable from the Start node, so nothing would ever run it.");
        }

        foreach (var node in Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal))
        {
            ValidateNode(node, errors);
        }

        // Parallel edges over one pair are legal and are how an author says "either of these"; two UNCONDITIONAL ones
        // are not, because the second could only ever repeat the first.
        foreach (var pair in Edges.GroupBy(static edge => (edge.From, edge.To)))
        {
            var unconditional = pair.Where(static edge => edge.Condition is null).ToList();
            if (unconditional.Count > 1)
            {
                errors.Add(new GraphWorkflowValidationError(unconditional[1].Key,
                    $"Edge {unconditional[1]} is the second unconditional edge between the same two nodes. "
                    + "Two edges over one pair are how a branch is widened, so at most one of them may be unconditional."));
            }
        }
    }

    private void ValidateNode(GraphWorkflowGraphNode node, List<GraphWorkflowValidationError> errors)
    {
        var outbound = OutboundEdges(node.NodeKey);
        if (node.Kind == GraphWorkflowNodeKind.End)
        {
            if (outbound.Count > 0)
            {
                errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                    $"Node '{node.NodeKey}' is an End node and something leaves it. A run stops there."));
            }
        }
        else if (outbound.Count == 0)
        {
            errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' is a {node.Kind} node with no outbound edge, so a run reaching it would stop without reaching an End."));
        }

        // joinPolicy is a property of EVERY node, not only of Join nodes. Reading it off Join alone is the documented
        // trap: an ordinary node with two inbound edges joins them too.
        if (node.JoinPolicy == GraphWorkflowJoinPolicy.Any && InboundEdges(node.NodeKey).Count < 2)
        {
            errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' declares joinPolicy 'Any' with fewer than two inbound edges. "
                + "One edge makes it an 'All' written confusingly, and none would never fire."));
        }

        if (node.Kind == GraphWorkflowNodeKind.Condition)
        {
            ValidateCondition(node, outbound, errors);
        }

        if (node.Config is GraphWorkflowPauseConfig pause)
        {
            ValidatePause(node, pause, outbound, errors);
        }
    }

    /// <summary>
    ///     A Condition node exists to choose, so it needs at least two ways out — and at most one of them may be the
    ///     unconditional default, because a second default is a branch that never loses.
    /// </summary>
    private static void ValidateCondition(GraphWorkflowGraphNode node, IReadOnlyList<GraphWorkflowGraphEdge> outbound, List<GraphWorkflowValidationError> errors)
    {
        if (outbound.Count < 2)
        {
            errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' is a Condition node with {outbound.Count} outbound edge(s). A choice needs at least two."));
        }

        var unconditional = outbound.Count(static edge => edge.Condition is null);
        if (unconditional > 1)
        {
            errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' is a Condition node carrying {unconditional} unconditional edges. "
                + "At most one of them may be the default; the rest have to say when they fire."));
        }
    }

    /// <summary>
    ///     The pre-flight rule: every decision this pause offers has somewhere to go. Asked through the state machine's
    ///     own routing over the document a pause actually stores, never by re-reading the condition — so the rule and
    ///     the routing cannot disagree about which answer reaches which branch.
    /// </summary>
    private static void ValidatePause(GraphWorkflowGraphNode node,
        GraphWorkflowPauseConfig pause,
        IReadOnlyList<GraphWorkflowGraphEdge> outbound,
        List<GraphWorkflowValidationError> errors)
    {
        foreach (var decision in pause.AllowedDecisions.Where(decision => !outbound.Any(edge => GraphWorkflowStateMachine.DecisionEdgeFires(edge, decision))))
        {
            errors.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' offers the decision {decision} and no edge out of it fires on that answer, "
                + "so answering it would strand the run."));
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
                throw new GraphWorkflowValidationException($"The graph workflow definition has a cycle through node '{nodeKey}'. Graph workflows are acyclic.");
            }

            foreach (var edge in OutboundEdges(nodeKey))
            {
                Walk(edge.To);
            }

            _ = onPath.Remove(nodeKey);
            _ = finished.Add(nodeKey);
        }
    }

    /// <summary>A key that reaches a database column, an element id and a URL, so it is held to one charset.</summary>
    private static string RequiredKey(JsonElement element, string name, string owner)
    {
        var key = RequiredString(element, name, owner);
        return key.Length <= MaxKeyLength && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? key
            : throw new GraphWorkflowValidationException($"The '{name}' '{key}' is not a legal key. Keys are 1 to {MaxKeyLength} characters of "
                                                         + "letters, digits, '_' and '-'.");
    }

    private static string RequiredString(JsonElement element, string name, string owner)
    {
        var value = OptionalString(element, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new GraphWorkflowValidationException($"{char.ToUpperInvariant(owner[0])}{owner[1..]} needs a non-empty '{name}'.")
            : value;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    ///     An optional string, trimmed, with a blank one read as ABSENT rather than refused. A cleared picker sends
    ///     <c>""</c> and older documents already hold one, and a run that cannot be routed because a field says nothing
    ///     is a worse answer than the field simply not applying.
    /// </summary>
    private static string? TrimmedOptionalString(JsonElement element, string name) =>
        OptionalString(element, name)?.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>An optional dot path, held to the same rule a condition's own path is.</summary>
    private static string? ParseDotPath(JsonElement element, string name, string nodeKey)
    {
        var path = TrimmedOptionalString(element, name);
        if (path is null || GraphWorkflowTokens.IsDotPath(path))
        {
            return path;
        }

        throw new GraphWorkflowValidationException($"The '{name}' '{path}' on node '{nodeKey}' is not a dot path. {DotPathRule}");
    }

    private static JsonElement? OptionalElement(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;

    private static JsonElement? OptionalObject(JsonElement element, string name, string nodeKey)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : throw new GraphWorkflowValidationException($"The '{name}' on node '{nodeKey}' must be an object.");
    }

    private static bool OptionalBool(JsonElement element, string name, string nodeKey, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new GraphWorkflowValidationException($"The '{name}' on node '{nodeKey}' must be true or false.")
        };
    }

    /// <summary>
    ///     A required enum member BY NAME. Never <c>Enum.TryParse</c> on its own: that accepts a numeric token, so
    ///     <c>"kind": "9"</c> would parse into a value no member has and reach the per-kind config table as a missing
    ///     key rather than as the refusal an author can read.
    /// </summary>
    private static TEnum RequiredEnum<TEnum>(JsonElement element, string name, string owner)
        where TEnum : struct, Enum =>
        GraphWorkflowTokens.TryParseName<TEnum>(OptionalString(element, name), out var parsed)
            ? parsed
            : throw new GraphWorkflowValidationException($"{char.ToUpperInvariant(owner[0])}{owner[1..]} needs a '{name}' from {string.Join(", ", Enum.GetNames<TEnum>())}.");

    private static TEnum OptionalEnum<TEnum>(JsonElement element, string name, string nodeKey, TEnum fallback)
        where TEnum : struct, Enum
    {
        var raw = OptionalString(element, name);
        if (raw is null)
        {
            return fallback;
        }

        return GraphWorkflowTokens.TryParseName<TEnum>(raw, out var parsed)
            ? parsed
            : throw new GraphWorkflowValidationException($"Node '{nodeKey}' has an unknown '{name}' of '{raw}'; expected one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
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
            : throw new GraphWorkflowValidationException($"Node '{nodeKey}' has a '{name}' of '{raw}', which is not a GUID.");
    }

    private static int? OptionalPositiveInt(JsonElement element, string name, string nodeKey) =>
        OptionalBoundedInt(element, name, nodeKey, minimum: 1, "must be positive");

    private static int? OptionalBoundedInt(JsonElement element, string name, string nodeKey, int minimum, string complaint)
    {
        if (OptionalInt(element, name, nodeKey) is not { } value)
        {
            return null;
        }

        return value >= minimum
            ? value
            : throw new GraphWorkflowValidationException($"The '{name}' on node '{nodeKey}' {complaint}.");
    }

    private static int? OptionalInt(JsonElement element, string name, string nodeKey)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new GraphWorkflowValidationException($"The '{name}' on node '{nodeKey}' must be a whole number.");
    }
}
