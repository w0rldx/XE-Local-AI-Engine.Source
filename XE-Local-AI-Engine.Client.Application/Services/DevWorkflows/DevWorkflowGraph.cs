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
///     (<c>requiredCapabilities</c>) that nothing dispatches on in v1, and parsing them into properties no code reads
///     would be shape without meaning. Unknown properties survive in the stored blob either way — this projection is
///     not a re-serialization of it.
///     <para>
///         <see cref="ModelProfile" /> and <see cref="ReasoningEffort" /> ARE read: an agent node's work session is
///         created with them as its own pins, beating the bound agent definition's. Only their SHAPE is checked here —
///         a model name is matched against this node's catalog at dispatch, exactly as an agent definition's pin is,
///         so a graph does not become unsaveable because a model was uninstalled after it was authored.
///     </para>
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
    DevWorkflowToolMode ToolMode,
    string? ModelProfile,
    string? ReasoningEffort);

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

    /// <summary>
    ///     The reasoning efforts a node may name, which are the ones an agent definition may pin
    ///     (<c>AgentDefinitionService</c>'s own list) — the override has to be sayable in the same vocabulary as the
    ///     pin it replaces. Not an enum: this travels to the provider as the string it is written as, except
    ///     <c>auto</c>, which the node resolves per turn into one of the others before anything is sent.
    /// </summary>
    private static readonly string[] ReasoningEfforts = ["none", "low", "medium", "high", "auto"];

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
        TerminalNodeKeys = new HashSet<string>(nodes.Keys.Where(key => _outbound[key].Count == 0), StringComparer.Ordinal);
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

    /// <summary>
    ///     Nodes no edge leaves — what "the run got somewhere" means. A run is <c>Completed</c> only once one of
    ///     these SUCCEEDED, so a tail that was skipped or abandoned cannot read as the run having done its job.
    ///     <para>
    ///         Read off the graph AS MATERIALIZED, which is what makes it right for a decomposing run: a clone's leaf
    ///         edge is wired to the join when it is created, so success routes through the join rather than through
    ///         any one clone. Whether a <see cref="TemplateKeys" /> node lands in this set is moot rather than ruled
    ///         out — a template never gets a node run, and the completion predicate reads node-run ROWS, so a template
    ///         key can neither satisfy it nor block it however the definition happens to wire it.
    ///     </para>
    /// </summary>
    public IReadOnlySet<string> TerminalNodeKeys { get; }

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
    ///     Every node that reaches <paramref name="to" /> by following out-edges, excluding itself — the mirror of
    ///     <see cref="Descendants" />, over the inbound index.
    ///     <para>
    ///         This is what "upstream of" means on a graph with more than one branch: a node on a PARALLEL branch is
    ///         neither an ancestor nor a descendant, and the whole point of asking is to leave it alone.
    ///     </para>
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
    ///     Whether a materialization's template carries a <c>DevTask</c> ANYWHERE. That is the node type whose clone
    ///     becomes a Development coder attempt, so it is what decides whether the coder's contract applies at all: the
    ///     attempt must export a NON-EMPTY patch, and a task written for it has to name the files it changes.
    ///     <para>
    ///         The whole subtree rather than its root, because a custom template is free to root itself in an Agent that
    ///         briefs a DevTask below it. Shared between the materializer, which REFUSES a task package on it, and the
    ///         agent executor, which appends the contract text on it, so what a decomposition is judged by cannot drift
    ///         from what it was told.
    ///     </para>
    /// </summary>
    public bool TemplateSubtreeHasDevTask(DevWorkflowMaterialization materialization) =>
        TemplateSubtree(materialization).Any(key => Nodes.TryGetValue(key, out var node) && node.NodeType == DevWorkflowNodeType.DevTask);

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
            toolMode,
            TrimmedOptionalString(element, "modelProfile"),
            ParseReasoningEffort(element, nodeKey));
    }

    /// <summary>
    ///     The node's reasoning-effort override, checked against the four the agent surface itself accepts
    ///     (<c>AgentDefinitionService</c>'s own set). An unknown token is refused here rather than dropped at dispatch:
    ///     unlike a model name, this vocabulary is closed and cannot go stale between authoring and a run.
    /// </summary>
    private static string? ParseReasoningEffort(JsonElement element, string nodeKey)
    {
        var effort = TrimmedOptionalString(element, "reasoningEffort");
        if (effort is null || ReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            return effort;
        }

        throw new DevWorkflowValidationException($"Node '{nodeKey}' has an unknown 'reasoningEffort' of '{effort}'; expected one of {string.Join(", ", ReasoningEfforts)}.");
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
        EnsureAppliesAreGated();

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

            // A template node is never instantiated under its own key — the seeding skips it and the materializer gives
            // each clone a key of its own, rewriting a retryTarget only for the clones INSIDE the subtree. So a node
            // outside one naming a template key names a node run no run ever has, and the route would block on
            // Configuration rather than re-attempt anything: a fix loop that reads correctly and cannot fire. The
            // clone-internal case is the one this is for, and it stays legal because both keys are rewritten together.
            if (TemplateKeys.Contains(node.RetryTarget!) && !TemplateKeys.Contains(node.NodeKey))
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares retryTarget '{node.RetryTarget}', which is a materialization template node. "
                                                         + $"'{node.RetryTarget}' is cloned once per task and never runs under that key, so no run would have a node "
                                                         + $"run to route to. Name a node outside the template subtree, or move '{node.NodeKey}' into it.");
            }
        }
    }

    /// <summary>
    ///     Y3 made structural: an apply node is reached from a human gate and from nothing else.
    ///     <para>
    ///         The rule the whole integration stage rests on is that no AI-authored patch reaches a real repository
    ///         without an operator decision recorded in the run's own audit trail. A definition is the only place that
    ///         can be checked before the fact: by the time an ungated apply runs, the approval it should have waited
    ///         for does not exist to be missed.
    ///     </para>
    ///     <para>
    ///         Stated as "every inbound edge comes from a human gate" rather than as "some gate lies on every path",
    ///         which is weaker in the way that matters: an approval given before the patches existed — a plan gate, say
    ///         — would satisfy the path reading while approving something else entirely. The immediate reading also
    ///         leaves no window between the answer and the act for a node run to change what is being applied.
    ///     </para>
    ///     <para>
    ///         The SOURCE being a gate is only half of it, and on its own it is not the rule at all: all three answers a
    ///         gate takes leave it <c>Succeeded</c> — a rejection reaches the run through an out-edge that matches
    ///         nothing, never through a node failure — so an unconditional gate-to-apply edge fires on a REJECTION and
    ///         applies the patches the operator declined. The condition is therefore checked too, and checked by asking
    ///         the dispatcher's own routing what it would do with each answer.
    ///     </para>
    /// </summary>
    private void EnsureAppliesAreGated()
    {
        foreach (var apply in Nodes.Values.Where(static node => node.ToolMode == DevWorkflowToolMode.Apply).Select(static node => node.NodeKey))
        {
            if (TemplateKeys.Contains(apply))
            {
                throw new DevWorkflowValidationException($"Node '{apply}' applies approved patches and is inside a materialization template, so every child would "
                                                         + "apply the whole fan-out again. Integration runs once, after the join.");
            }

            var inbound = InboundEdges(apply);
            if (inbound.Count == 0 || inbound.Any(edge => Nodes[edge.From].NodeType != DevWorkflowNodeType.HumanGate))
            {
                throw new DevWorkflowValidationException($"Node '{apply}' applies approved patches and is reached from something other than a human gate. An apply "
                                                         + "changes a real repository, so the decision that lets it happen has to be the step in front of it.");
            }

            EnsureCarriesOnlyTheApproval(apply, inbound);
        }
    }

    /// <summary>
    ///     The condition half of the rule above: an apply's inbound edges carry the approval and carry nothing else.
    ///     <para>
    ///         Asked through <see cref="DevWorkflowStateMachine.GateEdgeFires" />, over the set of answers
    ///         <see cref="DevWorkflowStateMachine.GateAnswers" /> derives from the transition table — so the document
    ///         evaluated here is the document the tick composes, the evaluation is the one the tick performs, and the
    ///         three answers are the three the tick can route. Re-deriving any of it — reading the condition and asking
    ///         whether it compares <c>decision</c> to <c>Approve</c> — would be a second account of routing that could
    ///         drift from the first, and that drift would be silent and would end in an apply.
    ///     </para>
    /// </summary>
    private static void EnsureCarriesOnlyTheApproval(string apply, IReadOnlyList<DevWorkflowGraphEdge> inbound)
    {
        foreach (var edge in inbound)
        {
            foreach (var decision in DevWorkflowStateMachine.GateAnswers)
            {
                var fires = DevWorkflowStateMachine.GateEdgeFires(edge, decision);
                if (decision == DevWorkflowDecisionKind.Approve && !fires)
                {
                    throw new DevWorkflowValidationException($"Node '{apply}' applies approved patches, and the edge {edge} does not carry an approval: it is false "
                                                             + "when the gate is answered Approve, so the one answer that may reach an apply never would.");
                }

                if (decision != DevWorkflowDecisionKind.Approve && fires)
                {
                    throw new DevWorkflowValidationException($"Node '{apply}' applies approved patches, and the edge {edge} also carries a {decision} answer. Every "
                                                             + "answer succeeds the gate and routing is the edge's own job, so this edge would apply the patches the "
                                                             + "operator declined. Condition it on the approval.");
                }
            }
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

    /// <summary>
    ///     An optional string, trimmed, with a blank one read as ABSENT rather than refused. A cleared picker sends
    ///     <c>""</c> and older documents already hold one, and a run that cannot be routed because a field says nothing
    ///     is a worse answer than the field simply not applying.
    /// </summary>
    private static string? TrimmedOptionalString(JsonElement element, string name) =>
        OptionalString(element, name)?.Trim() is { Length: > 0 } value ? value : null;

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
