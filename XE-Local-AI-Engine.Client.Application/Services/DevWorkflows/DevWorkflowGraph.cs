namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>The parsed, in-memory projection of a run's pinned <c>graph_json</c> — the source of routing truth.</summary>
/// <remarks>
///     Never persisted separately: there is no run-edge table, and materialization rewrites the run's own blob rather
///     than adding rows to one.
/// </remarks>
internal sealed class DevWorkflowGraph
{
    private const int SupportedSchemaVersion = 1;

    /// <summary>The reasoning efforts a node may name, which are the ones an agent definition may pin.</summary>
    /// <remarks>
    ///     <c>AgentDefinitionService</c> holds that list; the override has to be sayable in the same vocabulary as the
    ///     pin it replaces. Not an enum: this travels to the provider as the string it is written as, except
    ///     <c>auto</c>, which the node resolves per turn into one of the others before anything is sent.
    /// </remarks>
    private static readonly string[] ReasoningEfforts = ["none", "low", "medium", "high", "auto"];

    /// <summary>Defaults for a node that names none. Human waits and inline decisions get one try; work gets three.</summary>
    private const int DefaultWorkNodeMaxAttempts = 3;

    /// <summary>The most children one decomposition may expand into.</summary>
    /// <remarks>
    ///     The materialization transaction rewrites the run's whole encrypted graph blob, so the width of a fan-out is
    ///     the size of that write — bounded here, at the one place a definition can ask for it, rather than discovered
    ///     when a run tries to commit it.
    /// </remarks>
    private const int MaxTemplateChildren = 20;

    /// <summary>The longest reason an author may write beside a declared capability.</summary>
    /// <remarks>
    ///     It is a one-line justification a reviewer reads next to the node, and the whole graph document is encrypted
    ///     and rewritten on every materialization, so it is bounded where it is authored, not where it is stored.
    /// </remarks>
    private const int MaxCapabilityReasonLength = 200;

    /// <summary>What a node that routes rather than acts can change: nothing.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> NoEffects = new HashSet<DevWorkflowNodeEffect>();

    /// <summary>What a node that writes and nothing else can change. How FAR is the scope, which is asked separately.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> WriteEffects = new HashSet<DevWorkflowNodeEffect>
    {
        DevWorkflowNodeEffect.WriteExecute
    };

    /// <summary>A validation that names commands the project's catalog owns, none of which reaches the network.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> LocalValidationEffects = new HashSet<DevWorkflowNodeEffect>
    {
        DevWorkflowNodeEffect.ReadLocal
    };

    /// <summary>A validation that restores packages, or one whose command set is not knowable until the run picks it up.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> NetworkValidationEffects =
        new HashSet<DevWorkflowNodeEffect>
        {
            DevWorkflowNodeEffect.ReadLocal,
            DevWorkflowNodeEffect.Network
        };

    private readonly Dictionary<string, List<DevWorkflowGraphEdge>> _inbound;
    private readonly Dictionary<string, List<DevWorkflowGraphEdge>> _outbound;

    private DevWorkflowGraph(IReadOnlyDictionary<string, DevWorkflowGraphNode> nodes, IReadOnlyList<DevWorkflowGraphEdge> edges, bool allowUngatedWrites)
    {
        Nodes = nodes;
        Edges = edges;
        AllowUngatedWrites = allowUngatedWrites;
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

    /// <summary>The template's own opt-out of the gate requirement on a repository-scoped write.</summary>
    /// <remarks>
    ///     Absent means <c>false</c>, which keeps a definition written before this field byte-identical: the rule it
    ///     waives is new, so nothing already stored can be relying on the waiver.
    /// </remarks>
    public bool AllowUngatedWrites { get; }

    /// <summary>Nodes with no inbound edge — Start being implicit, this is what "entry node" means.</summary>
    /// <remarks>
    ///     It is also why a materialization template node, which is deliberately unreachable, must be excluded before
    ///     this is read as the set to materialize at run start.
    /// </remarks>
    public IReadOnlyList<string> EntryNodeKeys { get; }

    /// <summary>Every node of every template SUBTREE — each template root plus what it reaches short of its join.</summary>
    /// <remarks>
    ///     These are the nodes a run does NOT give a row to at start: they are clones-in-waiting, and one given a row
    ///     would wait forever on a source the run never instantiates.
    /// </remarks>
    public IReadOnlySet<string> TemplateKeys { get; }

    /// <summary>Nodes no edge leaves — what "the run got somewhere" means.</summary>
    /// <remarks>
    ///     A run is <c>Completed</c> only once one of these SUCCEEDED, so a tail that was skipped or abandoned cannot
    ///     read as the run having done its job. Read off the graph AS MATERIALIZED: a clone's leaf edge is wired to
    ///     the join when it is created, so success routes through the join rather than through any one clone. Whether
    ///     a <see cref="TemplateKeys" /> node lands here is moot — the completion predicate reads node-run ROWS.
    /// </remarks>
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

    /// <summary>Every node that reaches <paramref name="to" /> by following out-edges, excluding itself.</summary>
    /// <remarks>
    ///     The mirror of <see cref="Descendants" />, over the inbound index. This is what "upstream of" means on a
    ///     graph with more than one branch: a node on a PARALLEL branch is neither an ancestor nor a descendant, and
    ///     the whole point of asking is to leave it alone.
    /// </remarks>
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

    /// <summary>One template's nodes: its root and everything reachable from it WITHOUT passing through the join.</summary>
    /// <remarks>
    ///     The join is where a template subtree hands its work back to the graph, so it belongs to the graph and not
    ///     to the template — walking through it would swallow the whole rest of the run into the set of nodes a run
    ///     start refuses to instantiate.
    /// </remarks>
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

    /// <summary>What a node can change: DECLARED for an Agent, derived for every other node type.</summary>
    /// <remarks>
    ///     Those are the two honest answers: an agent node's reach follows from the definition it binds, resolved at
    ///     dispatch and unknowable here, so a declared write is a real one, while every other type says what it does
    ///     in the node itself. A <c>Tool</c> in <c>Validate</c> mode reads, and reaches the network when it names the
    ///     restore command or names none — a node naming none inherits the project profile's set, chosen when the run
    ///     picks a project up, so this fails toward the wider set rather than guessing the narrower one.
    /// </remarks>
    public static IReadOnlySet<DevWorkflowNodeEffect> Effects(DevWorkflowGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.NodeType switch
        {
            DevWorkflowNodeType.Agent => node.RequiredCapabilities,
            DevWorkflowNodeType.DevTask => WriteEffects,
            DevWorkflowNodeType.Tool when node.ToolMode == DevWorkflowToolMode.Apply => WriteEffects,
            DevWorkflowNodeType.Tool => node.ValidationCommandIds.Count == 0
                                        || node.ValidationCommandIds.Contains(DevelopmentCommandIds.DotnetRestore, StringComparer.Ordinal)
                ? NetworkValidationEffects
                : LocalValidationEffects,
            _ => NoEffects
        };
    }

    /// <summary>How far a node's write reaches.</summary>
    /// <remarks>
    ///     The one derived bit separating work done inside the node's own sandbox from work done to the operator's
    ///     repository (ruling D8): a <c>DevTask</c> runs against a worktree under this node's data root and its patch
    ///     reaches a real repository only through an apply node, while an apply node — and an Agent that declares a
    ///     write — reaches the repository itself.
    /// </remarks>
    public static DevWorkflowEffectScope ScopeOf(DevWorkflowGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.NodeType == DevWorkflowNodeType.DevTask ? DevWorkflowEffectScope.Sandbox : DevWorkflowEffectScope.Repository;
    }

    /// <summary>Whether a materialization's template carries a <c>DevTask</c> ANYWHERE.</summary>
    /// <remarks>
    ///     That node type's clone becomes a Development coder attempt, so it decides whether the coder's contract
    ///     applies at all. Shared between the materializer, which REFUSES a task package on it, and the agent
    ///     executor, which appends the contract text on it, so what a decomposition is judged by cannot drift from
    ///     what it was told. See docs/wiki/25-dev-workflows.md ("The clones, the join, and the zero-task case").
    /// </remarks>
    public bool TemplateSubtreeHasDevTask(DevWorkflowMaterialization materialization) =>
        TemplateSubtree(materialization).Any(key => Nodes.TryGetValue(key, out var node) && node.NodeType == DevWorkflowNodeType.DevTask);

    /// <summary>Parses the graph and enforces every structural rule; parsing IS the validation here.</summary>
    /// <remarks>
    ///     A graph that survives this is one the dispatcher can route without a second opinion. Run start re-validates
    ///     whatever validated the definition at save time, because an agent definition can be deleted in between. A
    ///     caller that OMITS the cap parses a graph already capped when it was saved: the graph cache and run start
    ///     re-parse stored graphs, and a cap lowered since would make a live run unroutable, not merely unsaveable.
    /// </remarks>
    /// <param name="maxNodes">Node cap, checked against the declared array before any node is read: the cap bounds this parse's work. A number, not an option, so this stays testable.</param>
    public static DevWorkflowGraph Parse(string graphJson, int maxNodes = int.MaxValue)
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

        var nodes = ParseNodes(root, maxNodes);
        var edges = ParseEdges(root, nodes);
        var graph = new DevWorkflowGraph(nodes, edges, OptionalFlag(root, "allowUngatedWrites"));
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
            throw new DevWorkflowValidationException($"The workflow graph is not valid JSON: {exception.Message}", exception);
        }
    }

    private static Dictionary<string, DevWorkflowGraphNode> ParseNodes(JsonElement root, int maxNodes)
    {
        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new DevWorkflowValidationException("A workflow graph needs a 'nodes' array.");
        }

        // The cap bites on the DECLARED length, before a single node is read: everything after this walks the graph, and
        // only the body size would otherwise bound it. Duplicate keys are refused below, so length and count agree here.
        var declared = nodesElement.GetArrayLength();
        if (declared > maxNodes)
        {
            throw new DevWorkflowValidationException($"The workflow graph declares {declared} nodes, more than the {maxNodes} one definition may carry.");
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
        var retryTarget = OptionalString(element, "retryTarget");

        return new DevWorkflowGraphNode
        {
            NodeKey = nodeKey,
            NodeType = nodeType,
            Label = OptionalString(element, "label") ?? nodeKey,
            AgentDefinitionId = OptionalGuid(element, "agentDefinitionId", nodeKey),
            AgentSeedSlug = OptionalString(element, "agentSeedSlug"),
            Instructions = OptionalString(element, "instructions"),
            ValidationCommandIds = commandIds,
            JoinPolicy = OptionalEnum(element, "joinPolicy", nodeKey, DevWorkflowJoinPolicy.All),
            MaxAttempts = OptionalPositiveInt(element, "maxAttempts", nodeKey) ?? (isWorkNode ? DefaultWorkNodeMaxAttempts : 1),
            RetryDelaySeconds = OptionalNonNegativeInt(element, "retryDelaySeconds", nodeKey) ?? 0,
            NodeTimeoutSeconds = OptionalPositiveInt(element, "nodeTimeoutSeconds", nodeKey),
            RetryTarget = retryTarget,
            Materialization = ParseMaterialization(element, nodeKey),
            ToolMode = toolMode,
            ModelProfile = TrimmedOptionalString(element, "modelProfile"),
            ReasoningEffort = ParseReasoningEffort(element, nodeKey),
            RequiredCapabilities = ParseRequiredCapabilities(element, nodeKey, nodeType),
            MaxLoopIterations = ParseMaxLoopIterations(element, nodeKey, retryTarget)
        };
    }

    /// <summary>The node's DECLARED effects: effect tokens keyed to the author's one-line reason for each.</summary>
    /// <remarks>
    ///     An object rather than an array because the reason is the half that makes a declaration reviewable, and it
    ///     is the shape the wire contract has carried since v1. An <c>Agent</c> node only: every other type's effects
    ///     follow from what it runs. Only the keys are kept — the reason is bounded here and then left in the stored
    ///     blob for the editor, and never quoted back in a validation message, which names keys and tokens only.
    /// </remarks>
    private static IReadOnlySet<DevWorkflowNodeEffect> ParseRequiredCapabilities(JsonElement element, string nodeKey, DevWorkflowNodeType nodeType)
    {
        if (!element.TryGetProperty("requiredCapabilities", out var declared) || declared.ValueKind == JsonValueKind.Null)
        {
            return NoEffects;
        }

        if (nodeType != DevWorkflowNodeType.Agent)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' declares 'requiredCapabilities' but is a {nodeType} node, and only an Agent node's reach is "
                                                     + "declared. Every other node type says what it does in the node itself, so a declaration here would be read by "
                                                     + "nothing — and a write declared where no rule looks is the silence these invariants exist to remove.");
        }

        if (declared.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException($"The 'requiredCapabilities' on node '{nodeKey}' must be an object whose keys are effects and whose values say why "
                                                     + $"the node needs each one; expected keys from {string.Join(", ", Enum.GetNames<DevWorkflowNodeEffect>())}.");
        }

        var effects = new HashSet<DevWorkflowNodeEffect>();
        foreach (var capability in declared.EnumerateObject())
        {
            // By NAME: Enum.TryParse would take "3" and declare an effect no member has, which every later
            // capability check reads as a value it does not know rather than as the refusal an author can act on.
            if (!GraphWorkflowTokens.TryParseName<DevWorkflowNodeEffect>(capability.Name, out var effect))
            {
                throw new DevWorkflowValidationException($"Node '{nodeKey}' declares an unknown capability '{capability.Name}'; "
                                                         + $"expected one of {string.Join(", ", Enum.GetNames<DevWorkflowNodeEffect>())}.");
            }

            if (capability.Value.ValueKind != JsonValueKind.String || capability.Value.GetString() is not { Length: > 0 } reason)
            {
                throw new DevWorkflowValidationException($"The capability '{effect}' on node '{nodeKey}' needs a reason, written as a non-empty string. A declared "
                                                         + "effect widens what the node may do, so the definition has to say what for.");
            }

            if (reason.Length > MaxCapabilityReasonLength)
            {
                throw new DevWorkflowValidationException($"The reason for capability '{effect}' on node '{nodeKey}' is longer than {MaxCapabilityReasonLength} characters. "
                                                         + "It is a one-line justification, not the node's instructions.");
            }

            _ = effects.Add(effect);
        }

        return effects;
    }

    /// <summary>How many times this node's fix loop may re-run before the run stops and asks a human.</summary>
    /// <remarks>
    ///     Refused on a node that names no <c>retryTarget</c>, the way <see cref="ParseToolMode" /> refuses a mode on
    ///     a node that runs none: a field that does nothing where it is written is a definition saying something the
    ///     runtime will not do. Absent means NO per-loop cap (ruling D9) — a parse-time default would silently tighten
    ///     routing on every stored definition, and cap human retries, which raise the same counter, on nodes without one.
    /// </remarks>
    private static int? ParseMaxLoopIterations(JsonElement element, string nodeKey, string? retryTarget)
    {
        // Every refusal about the cap carries the invariant id, the shared number parsers' own included: the id is what
        // the operator quotes and what the tests assert on, and a bare "must be positive" traces back to no rule.
        int? maxLoopIterations;
        try
        {
            maxLoopIterations = OptionalPositiveInt(element, "maxLoopIterations", nodeKey);
        }
        catch (DevWorkflowValidationException exception)
        {
            throw new DevWorkflowValidationException($"{exception.Message.TrimEnd('.')} (invariant GRAPH-C4-4).", exception);
        }

        if (maxLoopIterations is not null && retryTarget is null)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' declares a 'maxLoopIterations' but no 'retryTarget', so it has no fix loop to bound. "
                                                     + "The cap counts routes to a retry target, and this node routes none (invariant GRAPH-C4-4).");
        }

        return maxLoopIterations;
    }

    /// <summary>The node's reasoning-effort override, checked against the set the agent surface itself accepts.</summary>
    /// <remarks>
    ///     <c>AgentDefinitionService</c> owns that set. An unknown token is refused here rather than dropped at
    ///     dispatch: unlike a model name, this vocabulary is closed and cannot go stale between authoring and a run.
    /// </remarks>
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

        return new DevWorkflowMaterialization
        {
            TemplateNodeKey = RequiredString(materialization, "templateNodeKey", $"the materialization on node '{nodeKey}'"),
            ArtifactKind = RequiredEnum<DevWorkflowArtifactKind>(materialization, "artifactKind", $"the materialization on node '{nodeKey}'"),
            JoinNodeKey = RequiredString(materialization, "joinNodeKey", $"the materialization on node '{nodeKey}'"),
            MaxChildren = OptionalPositiveInt(materialization, "maxChildren", nodeKey)
                          ?? throw new DevWorkflowValidationException($"The materialization on node '{nodeKey}' needs a positive 'maxChildren'.")
        };
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

            // One edge per pair. Admission judges every inbound edge on its own, so a second edge whose condition does
            // not fire is DEAD and skips the target; refusing it also keeps the pair usable as the edge rewrite's key.
            if (!pairs.Add((from, to)))
            {
                throw new DevWorkflowValidationException($"The workflow graph declares edge {edge} twice. A second edge between the same two nodes "
                                                         + "cannot widen the first: each is judged on its own, and one whose condition does not fire skips the target.");
            }

            edges.Add(edge);
        }

        return edges;
    }

    /// <summary>The structural rules, each of which exists because breaking it produces a run that hangs.</summary>
    /// <remarks>
    ///     A run that hangs rather than one that fails: a cycle never terminates, an unreachable node never becomes
    ///     eligible, a one-edge <c>Any</c> is an <c>All</c> written confusingly, and a retry target that is not an
    ///     ancestor is the cycle by another name.
    /// </remarks>
    private void Validate()
    {
        // Which materializer owns each template node: a subtree is cloned once per task by its owner, and a zero-task
        // decomposition writes under the template's OWN key, so two claimants would seed one key twice and then refuse.
        var templateOwner = new Dictionary<string, string>(StringComparer.Ordinal);

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

                // The join has to FOLLOW the node that decomposes it: expansion wires every clone's leaf to the join, so
                // naming it or an ancestor is a cycle — one EnsureAcyclic, seeing the AUTHORED edges only, cannot see.
                if (string.Equals(materialization.JoinNodeKey, node.NodeKey, StringComparison.Ordinal)
                    || Ancestors(node.NodeKey).Contains(materialization.JoinNodeKey))
                {
                    throw new DevWorkflowValidationException($"The materialization on node '{node.NodeKey}' names join node '{materialization.JoinNodeKey}', which is "
                                                             + $"'{node.NodeKey}' itself or one of its ancestors. The join collects what the clones produced, so it has "
                                                             + "to follow the node that decomposes the work; an upstream join would route the expansion back into "
                                                             + "itself.");
                }

                ValidateTemplateSubtree(node.NodeKey, materialization, templateOwner);
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

        EnsureEveryPathReachesAnEnd();
        EnsureDeclaredWritesAreGated();
        EnsureAppliesFollowAValidation();

        foreach (var node in Nodes.Values.Where(static node => node.RetryTarget is not null))
        {
            EnsureDeclared(node.RetryTarget!, $"Node '{node.NodeKey}' declares retryTarget");
            if (!Descendants(node.RetryTarget!).Contains(node.NodeKey, StringComparer.Ordinal))
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares retryTarget '{node.RetryTarget}', which is not one of its ancestors. "
                                                         + "Routing a failure to a node that does not lead back here would livelock the run.");
            }

            // A template node never runs under its own key, so a node OUTSIDE the subtree naming one names a node run
            // that never exists. The clone-internal case stays legal: both keys are rewritten together.
            if (TemplateKeys.Contains(node.RetryTarget!) && !TemplateKeys.Contains(node.NodeKey))
            {
                throw new DevWorkflowValidationException($"Node '{node.NodeKey}' declares retryTarget '{node.RetryTarget}', which is a materialization template node. "
                                                         + $"'{node.RetryTarget}' is cloned once per task and never runs under that key, so no run would have a node "
                                                         + $"run to route to. Name a node outside the template subtree, or move '{node.NodeKey}' into it.");
            }
        }
    }

    /// <summary><c>GRAPH-C4-1</c> — every path reaches an end, over the edges a run can TAKE, not every authored one.</summary>
    /// <remarks>
    ///     An out-edge of a human gate that is false for all three answers can never fire, so the branch behind it is
    ///     written but unreachable. Three steps, in this order, because the order decides which complaint the operator
    ///     gets. Decidable for a HUMAN gate only: a <c>Gate</c> node's output is whatever it produced, so no
    ///     definition-time reading says which of its conditions will fire.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    private void EnsureEveryPathReachesAnEnd()
    {
        var dead = DeadGateEdges();
        if (dead.Count == 0)
        {
            return;
        }

        var reachesAnEnd = NodesThatReachAnEnd(dead);

        // The gates that own a dead edge come first, and only they get the sentence that says so: chaining two gates
        // strands every gate above the broken one, and an ordinal tie-break would name the wrong line to fix.
        var culprits = dead.Select(static edge => edge.From).ToHashSet(StringComparer.Ordinal);
        if (Nodes.Values.Where(node => !TemplateKeys.Contains(node.NodeKey) && !reachesAnEnd.Contains(node.NodeKey))
                 .OrderBy(node => culprits.Contains(node.NodeKey) ? 0 : 1)
                 .ThenBy(static node => node.NodeKey, StringComparer.Ordinal)
                 .FirstOrDefault() is { } stranded)
        {
            throw new DevWorkflowValidationException(culprits.Contains(stranded.NodeKey)
                ? $"Node '{stranded.NodeKey}' is a human gate with an out-edge that can never fire, and nothing it still reaches leads to an end of the run "
                  + "(invariant GRAPH-C4-1)."
                : $"Node '{stranded.NodeKey}' cannot reach an end of the run, because every path out of it passes through a gate edge that can never fire "
                  + "(invariant GRAPH-C4-1).");
        }

        var orphaned = dead.OrderBy(static edge => edge.From, StringComparer.Ordinal).ThenBy(static edge => edge.To, StringComparer.Ordinal).First();
        throw new DevWorkflowValidationException($"The edge {orphaned} leaves a human gate and is false for all three answers, so nothing would ever take it. "
                                                 + "Condition it on an answer the gate can give (invariant GRAPH-C4-1).");
    }

    /// <summary><c>GRAPH-C4-2</c> — a node that writes outside its sandbox is reached through a human gate.</summary>
    /// <remarks>
    ///     <see cref="EnsureAppliesAreGated" /> is strictly stronger for an apply node, so this never weakens it;
    ///     approval policy is tighten-only. What this adds is the DECLARED case: an Agent node whose author wrote
    ///     <c>WriteExecute</c> into <c>requiredCapabilities</c> is taken at their word. The waiver is the graph's own
    ///     <c>allowUngatedWrites</c>. See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    private void EnsureDeclaredWritesAreGated()
    {
        if (AllowUngatedWrites)
        {
            return;
        }

        var writers = Nodes.Values
                           .Where(static node => Effects(node).Contains(DevWorkflowNodeEffect.WriteExecute) && ScopeOf(node) == DevWorkflowEffectScope.Repository)
                           .ToList();
        if (writers.Count == 0)
        {
            return;
        }

        var gated = Assured(static node => node.NodeType == DevWorkflowNodeType.HumanGate);
        if (writers.FirstOrDefault(node => !gated.Contains(node.NodeKey)) is { } ungated)
        {
            throw new DevWorkflowValidationException($"Node '{ungated.NodeKey}' can write outside its sandbox and a run can reach it without an operator ever being "
                                                     + "asked. Put a human gate on every path into it, or set 'allowUngatedWrites' on this template and say why "
                                                     + "(invariant GRAPH-C4-2).");
        }
    }

    /// <summary><c>GRAPH-C4-3</c> — an apply follows a validation: every path into an <c>Apply</c> node passes a <c>Validate</c> Tool.</summary>
    /// <remarks>
    ///     Deliberately optimistic in one place rather than claiming an airtight proof: admission drops inbound edges
    ///     whose source is a template key, so the <c>validate(template) → join</c> edge is not a run-time dependency.
    ///     The dispatcher closes that gap operationally, re-asking the question over the rows a run actually landed.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    private void EnsureAppliesFollowAValidation()
    {
        var applies = Nodes.Values.Where(static node => node.ToolMode == DevWorkflowToolMode.Apply).ToList();
        if (applies.Count == 0)
        {
            return;
        }

        var validated = Assured(static node => node.NodeType == DevWorkflowNodeType.Tool && node.ToolMode == DevWorkflowToolMode.Validate);
        if (applies.FirstOrDefault(node => !validated.Contains(node.NodeKey)) is { } unvalidated)
        {
            throw new DevWorkflowValidationException($"Node '{unvalidated.NodeKey}' applies approved patches and a run can reach it without any validation node "
                                                     + "having run. Put a Tool node in Validate mode on every path into it (invariant GRAPH-C4-3).");
        }
    }

    /// <summary>Has EVERY run reaching this node already passed one with <paramref name="property" />?</summary>
    /// <remarks>
    ///     One forward fixpoint in topological order, shared by both invariants above, because two implementations of
    ///     the question would drift. <c>Assured(v) = P(v) || Combine(inbound of v)</c> with <c>Combine(∅) = false</c>,
    ///     so an entry node evaluates to <c>P(entry)</c>. <c>Combine</c> is keyed on <c>joinPolicy</c> and never on
    ///     node TYPE: OR under <c>All</c>, AND under <c>Any</c>.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    private HashSet<string> Assured(Func<DevWorkflowGraphNode, bool> property)
    {
        var inbound = AugmentedEdges().ToLookup(static edge => edge.To, StringComparer.Ordinal);
        var assured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in AncestorsFirst(inbound).Select(key => Nodes[key]))
        {
            var edges = inbound[node.NodeKey].ToList();
            var combined = edges.Count > 0
                           && (node.JoinPolicy == DevWorkflowJoinPolicy.All
                               ? edges.Exists(edge => assured.Contains(edge.From))
                               : edges.TrueForAll(edge => assured.Contains(edge.From)));
            if (property(node) || combined)
            {
                _ = assured.Add(node.NodeKey);
            }
        }

        return assured;
    }

    /// <summary>
    ///     A topological order of the augmented graph — every node after all its inbound sources — so one pass
    ///     computes the fixpoint.
    /// </summary>
    /// <remarks>
    ///     Safe because acyclicity is proven before any of this runs. On an EXPLICIT stack for the reason
    ///     <see cref="EnsureAcyclic(IReadOnlyList{DevWorkflowGraphEdge}, Func{string, IReadOnlyList{string}, string})" />
    ///     is: one frame per node is a process kill on a long chain, and any definition declaring a write or an apply
    ///     reaches this walk. The frame carries how far through the node's inbound edges the walk has got, which keeps
    ///     the order the recursive one produced — a source is EMITTED before the next is started, not merely marked.
    /// </remarks>
    private List<string> AncestorsFirst(ILookup<string, DevWorkflowGraphEdge> inbound)
    {
        var sources = Nodes.Keys.ToDictionary(key => key, key => inbound[key].ToList(), StringComparer.Ordinal);
        var order = new List<string>(Nodes.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string NodeKey, int EdgeIndex)>();
        foreach (var root in Nodes.Keys.Where(placed.Add))
        {
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var (nodeKey, edgeIndex) = pending.Pop();
                var entering = sources[nodeKey];
                if (edgeIndex == entering.Count)
                {
                    order.Add(nodeKey);
                    continue;
                }

                pending.Push((nodeKey, edgeIndex + 1));
                if (placed.Add(entering[edgeIndex].From))
                {
                    pending.Push((entering[edgeIndex].From, 0));
                }
            }
        }

        return order;
    }

    /// <summary>The out-edges of a human gate that no answer would take, asked of the dispatcher's own routing.</summary>
    /// <remarks>
    ///     Rather than re-derived by reading the condition — the same reason
    ///     <see cref="EnsureCarriesOnlyTheApproval" /> asks it that way, and the same drift it avoids.
    /// </remarks>
    private HashSet<DevWorkflowGraphEdge> DeadGateEdges() =>
    [
        .. Edges.Where(edge => Nodes[edge.From].NodeType == DevWorkflowNodeType.HumanGate
                               && !DevWorkflowStateMachine.GateAnswers.Any(answer => DevWorkflowStateMachine.GateEdgeFires(edge, answer)))
    ];

    /// <summary>Which nodes can still reach a member of <see cref="TerminalNodeKeys" /> once the dead edges are out.</summary>
    /// <remarks>
    ///     Walked backwards from the ends over the live edge set, which is one traversal rather than one per node.
    /// </remarks>
    private HashSet<string> NodesThatReachAnEnd(HashSet<DevWorkflowGraphEdge> dead)
    {
        var inbound = AugmentedEdges().Where(edge => !dead.Contains(edge)).ToLookup(static edge => edge.To, StringComparer.Ordinal);
        var reachesAnEnd = new HashSet<string>(TerminalNodeKeys, StringComparer.Ordinal);
        var pending = new Stack<string>(TerminalNodeKeys);
        while (pending.Count > 0)
        {
            foreach (var edge in inbound[pending.Pop()].Where(edge => reachesAnEnd.Add(edge.From)))
            {
                pending.Push(edge.From);
            }
        }

        return reachesAnEnd;
    }

    /// <summary>The authored edges plus one VIRTUAL edge from each materializing node to its template root.</summary>
    /// <remarks>
    ///     Not an invention: expansion wires exactly that edge, so the virtual one is the definition-time image of a
    ///     real run-time one and the invariants answer the same before and after materialization. A node may name
    ///     ITSELF as its own template root, so a self-edge is skipped; with it skipped the augmented graph is acyclic,
    ///     which rests on <see cref="Validate" />'s refusal of a join that IS the materializing node or an ancestor.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    private List<DevWorkflowGraphEdge> AugmentedEdges() =>
    [
        .. Edges,
        .. Nodes.Values.Where(static node => node.Materialization is not null)
                .Select(static node => new DevWorkflowGraphEdge(node.NodeKey, node.Materialization!.TemplateNodeKey, Condition: null))
                .Where(static edge => !string.Equals(edge.From, edge.To, StringComparison.Ordinal))
    ];

    /// <summary><c>Y3</c> made structural: an apply node is reached from a human gate and from nothing else.</summary>
    /// <remarks>
    ///     No AI-authored patch reaches a real repository without an operator decision in the run's own audit trail.
    ///     Stated as "every inbound edge comes from a human gate" rather than "some gate lies on every path", which
    ///     would let an approval given before the patches existed satisfy it. The SOURCE being a gate is only half:
    ///     all three answers leave a gate <c>Succeeded</c>, so the edge's condition is checked too.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
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

    /// <summary>The condition half of the rule above: an apply's inbound edges carry the approval and nothing else.</summary>
    /// <remarks>
    ///     Asked through <see cref="DevWorkflowStateMachine.GateEdgeFires" />, over
    ///     <see cref="DevWorkflowStateMachine.GateAnswers" />, so the document, the evaluation and the answers are the
    ///     tick's own. Re-deriving any of it would be a second account of routing, and that drift would be silent and
    ///     would end in an apply.
    /// </remarks>
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

    /// <summary>The structural rule on a template SUBTREE: nothing outside it points into it.</summary>
    /// <remarks>
    ///     Breaking it HANGS rather than fails: a node outside the template that depended on it would wait, at run
    ///     start and forever, on the one node deliberately never instantiated. "No edge leaves the subtree except to
    ///     the join" is true by construction, the subtree being everything the template reaches short of the join, so
    ///     the only way a live node is swallowed into a template is by being pointed at from outside it.
    /// </remarks>
    private void ValidateTemplateSubtree(string nodeKey, DevWorkflowMaterialization materialization, Dictionary<string, string> templateOwner)
    {
        var subtree = TemplateSubtree(materialization);
        foreach (var key in subtree)
        {
            // Exactly one materializer owns each template node: two sharing a subtree would both seed the SAME key with
            // a no-op verdict row once a decomposition found no work, deadlocking the run. Authored at save, refused there.
            if (!templateOwner.TryAdd(key, nodeKey) && !string.Equals(templateOwner[key], nodeKey, StringComparison.Ordinal))
            {
                throw new DevWorkflowValidationException($"Node '{key}' is inside the materialization template of '{templateOwner[key]}' and of '{nodeKey}'. A template "
                                                         + "subtree is cloned once per task by the node that owns it, and a decomposition that finds no work records "
                                                         + "its verdict under the template's own key — two owners would both try to write it. Give each "
                                                         + "decomposition a template of its own.");
            }

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

            // GRAPH-C4-1, step four: an edge-less template leaf would take the zero-task verdict row at a key the
            // completion predicate reads. Scoped to Tool/Validate, the only nodes that row is written for.
            if (Nodes[key] is { NodeType: DevWorkflowNodeType.Tool, ToolMode: DevWorkflowToolMode.Validate } && OutboundEdges(key).Count == 0)
            {
                throw new DevWorkflowValidationException($"Node '{key}' validates inside the materialization template of '{nodeKey}' and no edge leaves it, so it "
                                                         + "would count as an end of the run — and a decomposition that found no work records a succeeded verdict "
                                                         + $"under that key. Give it an edge to the join node '{materialization.JoinNodeKey}' (invariant "
                                                         + "GRAPH-C4-1).");
            }
        }
    }

    /// <summary>No cycle in the authored graph, and none in the AUGMENTED one either.</summary>
    /// <remarks>
    ///     The second walk is not a duplicate: two materializers whose templates lead into each other close a loop no
    ///     single-materializer rule can see, since only the virtual edges together make it a cycle. That would leave
    ///     <see cref="AncestorsFirst" /> answering out of topological order, and at run time the clone edges each
    ///     expansion wires turn it into a real cycle. Authored first, so a plain back edge reads as the plain
    ///     complaint; a graph with no materialization has one edge set and skips the second walk.
    /// </remarks>
    private void EnsureAcyclic()
    {
        EnsureAcyclic(Edges,
            static (nodeKey, _) => $"The workflow graph has a cycle through node '{nodeKey}'. A fix loop is a retryTarget, not a back edge.");

        var augmented = AugmentedEdges();
        if (augmented.Count == Edges.Count)
        {
            return;
        }

        EnsureAcyclic(augmented,
            (nodeKey, cycle) =>
            {
                var materializers = cycle.Where(key => Nodes[key].Materialization is not null).Select(static key => $"'{key}'").ToList();
                return $"Node '{nodeKey}' is on a cycle that only appears once the materializations of {string.Join(" and ", materializers)} are expanded. Each "
                       + "decomposition's clones are wired to its join, so templates that lead into one another would route each expansion into the other and the "
                       + "run would never finish. Point one of them at a template outside the other's reach.";
            });
    }

    /// <summary>Depth-first colouring: white unvisited, grey on the current path, black finished; a grey hit is a cycle.</summary>
    /// <remarks>
    ///     Walked on an EXPLICIT stack rather than by recursion, so a long chain costs heap rather than a frame per
    ///     node: a stack overflow is a process kill nothing can catch, and this parse runs on a thread-pool thread on
    ///     behalf of a request body. The frame is the node plus how far through its out-edges the walk has got, which
    ///     makes the iterative walk visit in the recursive order — one edge finished before the next is started.
    /// </remarks>
    private void EnsureAcyclic(IReadOnlyList<DevWorkflowGraphEdge> edges, Func<string, IReadOnlyList<string>, string> message)
    {
        // Keyed by every declared node rather than only by the ones an edge leaves, so a leaf is a lookup that answers
        // an empty list instead of a miss. Both endpoints of every edge are declared by the time this runs.
        var outbound = Nodes.Keys.ToDictionary(key => key, _ => new List<DevWorkflowGraphEdge>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            outbound[edge.From].Add(edge);
        }

        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string NodeKey, int EdgeIndex)>();
        foreach (var root in Nodes.Keys.Where(key => !finished.Contains(key)))
        {
            _ = onPath.Add(root);
            path.Add(root);
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var (nodeKey, edgeIndex) = pending.Pop();
                var leaving = outbound[nodeKey];
                if (edgeIndex == leaving.Count)
                {
                    _ = onPath.Remove(nodeKey);
                    path.RemoveAt(path.Count - 1);
                    _ = finished.Add(nodeKey);
                    continue;
                }

                pending.Push((nodeKey, edgeIndex + 1));
                var next = leaving[edgeIndex].To;
                if (finished.Contains(next))
                {
                    continue;
                }

                if (!onPath.Add(next))
                {
                    // The path from the repeated key onwards IS the cycle, which is what lets the message name the
                    // nodes that made it rather than the one node the walk happened to come back to.
                    throw new DevWorkflowValidationException(message(next, path[path.IndexOf(next)..]));
                }

                path.Add(next);
                pending.Push((next, 0));
            }
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

    /// <summary>An optional string, trimmed, with a blank one read as ABSENT rather than refused.</summary>
    /// <remarks>
    ///     A cleared picker sends <c>""</c> and older documents already hold one, and a run that cannot be routed
    ///     because a field says nothing is a worse answer than the field simply not applying.
    /// </remarks>
    private static string? TrimmedOptionalString(JsonElement element, string name) =>
        OptionalString(element, name)?.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>A boolean that is absent, null or <c>false</c> reads as false; a non-boolean is refused.</summary>
    /// <remarks>
    ///     A graph-level waiver written as <c>"true"</c> must not silently be no waiver, and must not silently be one
    ///     either.
    /// </remarks>
    private static bool OptionalFlag(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new DevWorkflowValidationException($"The '{name}' of a workflow graph must be true or false.")
        };
    }

    /// <summary>A required enum member BY NAME, never <c>Enum.TryParse</c> on its own.</summary>
    /// <remarks>
    ///     That accepts a numeric token, so <c>"nodeType": "3"</c> would parse into a value no member has and reach
    ///     the per-type config table as a missing key rather than as the refusal an author can read.
    /// </remarks>
    private static TEnum RequiredEnum<TEnum>(JsonElement element, string name, string owner)
        where TEnum : struct, Enum =>
        GraphWorkflowTokens.TryParseName<TEnum>(OptionalString(element, name), out var parsed)
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

        return GraphWorkflowTokens.TryParseName<TEnum>(raw, out var parsed)
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
