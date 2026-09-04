namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

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

/// <summary>
///     What a node can change. The tokens are the tool taxonomy's — <c>ToolCategory</c> — because an author writing a
///     capability should not have to learn a second vocabulary for the same idea, but the QUESTION is a different one:
///     the chat taxonomy asks whether a call needs an approval round-trip, and this asks what a node can change outside
///     its own sandbox. They share names, not meaning.
///     <para>
///         There is no <c>Unknown</c>. A node declaring nothing is judged by its DERIVED effects, which are total over
///         the seven node types, so there is never a node whose reach is unanswerable.
///     </para>
/// </summary>
internal enum DevWorkflowNodeEffect
{
    ReadLocal,
    WriteExecute,
    Orchestration,
    Network
}

/// <summary>
///     How far out a write reaches. One derived bit rather than a second taxonomy: a DevTask writes a worktree created
///     under the node's own data root, and its patch reaches the operator's repository only through the apply node a
///     human gate already stands in front of.
/// </summary>
internal enum DevWorkflowEffectScope
{
    Sandbox,
    Repository
}

/// <summary>The decomposition template a node expands into. All four fields are load-bearing.</summary>
internal sealed record DevWorkflowMaterialization(string TemplateNodeKey, DevWorkflowArtifactKind ArtifactKind, string JoinNodeKey, int MaxChildren);

/// <summary>
///     One node of the parsed graph. Only what the runtime reads; unknown properties survive in the stored blob either
///     way, because this projection is not a re-serialization of it.
///     <para>
///         <see cref="RequiredCapabilities" /> is the author's DECLARED effect set. Only the effects are kept: the
///         reason written beside each one is checked here (a token the vocabulary knows, a string, at most 200
///         characters) and then left in the blob for the editor to render, because nothing the runtime decides reads
///         it.
///     </para>
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
    string? ReasoningEffort,
    IReadOnlySet<DevWorkflowNodeEffect> RequiredCapabilities,
    int? MaxLoopIterations);

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

    /// <summary>
    ///     The longest reason an author may write beside a declared capability. It is a one-line justification a
    ///     reviewer reads next to the node, and the whole graph document is encrypted and rewritten on every
    ///     materialization, so it is bounded where it is authored rather than where it is stored.
    /// </summary>
    private const int MaxCapabilityReasonLength = 200;

    /// <summary>What a node that routes rather than acts can change: nothing.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> NoEffects = new HashSet<DevWorkflowNodeEffect>();

    /// <summary>What a node that writes and nothing else can change. How FAR is the scope, which is asked separately.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> WriteEffects = new HashSet<DevWorkflowNodeEffect> { DevWorkflowNodeEffect.WriteExecute };

    /// <summary>A validation that names commands the project's catalog owns, none of which reaches the network.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> LocalValidationEffects = new HashSet<DevWorkflowNodeEffect> { DevWorkflowNodeEffect.ReadLocal };

    /// <summary>A validation that restores packages, or one whose command set is not knowable until the run picks it up.</summary>
    private static readonly IReadOnlySet<DevWorkflowNodeEffect> NetworkValidationEffects =
        new HashSet<DevWorkflowNodeEffect> { DevWorkflowNodeEffect.ReadLocal, DevWorkflowNodeEffect.Network };

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

    /// <summary>
    ///     The template's own opt-out of the gate requirement on a repository-scoped write. Absent means <c>false</c>,
    ///     which is what keeps a definition written before this field byte-identical: the rule it waives is new, so
    ///     nothing already stored can be relying on the waiver.
    /// </summary>
    public bool AllowUngatedWrites { get; }

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
    ///     What a node can change. DECLARED for an Agent and derived for everything else, because those are the two
    ///     honest answers: an agent node's reach follows from the definition it binds, which is resolved at dispatch and
    ///     unknowable here, so an author who declares a write is declaring a real one — while every other node type says
    ///     what it does in the node itself.
    ///     <para>
    ///         A <c>Tool</c> in <c>Validate</c> mode reads, and reaches the network when it names the restore command
    ///         OR names no command at all: a node naming none inherits the project profile's set, which is chosen when
    ///         the run picks a project up, so the answer here fails toward the wider set rather than guessing the
    ///         narrower one.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     How far a node's write reaches. The one derived bit that separates work done inside the node's own sandbox
    ///     from work done to the operator's repository: a <c>DevTask</c> runs against a worktree created under this
    ///     node's data root and its patch reaches a real repository only through an apply node (D8), while an apply
    ///     node — and an Agent that declares a write — reaches the repository itself.
    /// </summary>
    public static DevWorkflowEffectScope ScopeOf(DevWorkflowGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.NodeType == DevWorkflowNodeType.DevTask ? DevWorkflowEffectScope.Sandbox : DevWorkflowEffectScope.Repository;
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
        var retryTarget = OptionalString(element, "retryTarget");

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
            retryTarget,
            ParseMaterialization(element, nodeKey),
            toolMode,
            TrimmedOptionalString(element, "modelProfile"),
            ParseReasoningEffort(element, nodeKey),
            ParseRequiredCapabilities(element, nodeKey, nodeType),
            ParseMaxLoopIterations(element, nodeKey, retryTarget));
    }

    /// <summary>
    ///     The node's DECLARED effects: an object whose keys are effect tokens and whose values are the author's
    ///     one-line reason for each. An object rather than an array because the reason is the half that makes a
    ///     declaration reviewable, and it is the shape the wire contract has carried since v1. An <c>Agent</c> node
    ///     only — every other node type's effects follow from what it runs, so a declaration on one is refused the way
    ///     <see cref="ParseToolMode" /> refuses a mode on a node that runs none.
    ///     <para>
    ///         Only the keys are kept. The reason is bounded here and then left in the stored blob for the editor,
    ///         because no routing decision reads it — and, for the same reason, it is never quoted back in a validation
    ///         message: these messages name node keys and vocabulary tokens only.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     How many times this node's fix loop may re-run before the run stops and asks a human. Refused on a node that
    ///     names no <c>retryTarget</c>, the way <see cref="ParseToolMode" /> refuses a mode on a node that runs none: a
    ///     field that does nothing where it is written is a definition saying something the runtime will not do.
    ///     <para>
    ///         Absent means NO per-loop cap (D9). A parse-time default would tighten routing on every already-stored
    ///         definition at run start, silently and with no author having asked for it — and, because an operator
    ///         <c>Retry</c> raises the same attempt counter, would cap human retries on nodes that have no cap today.
    ///     </para>
    /// </summary>
    private static int? ParseMaxLoopIterations(JsonElement element, string nodeKey, string? retryTarget)
    {
        // Every refusal about the cap carries the invariant id, the shared number parsers' own included: the id is what
        // the operator quotes and what the tests assert on, and a bare "must be positive" would be the one C4-4
        // complaint that cannot be traced back to the rule that raised it.
        int? maxLoopIterations;
        try
        {
            maxLoopIterations = OptionalPositiveInt(element, "maxLoopIterations", nodeKey);
        }
        catch (DevWorkflowValidationException exception)
        {
            throw new DevWorkflowValidationException($"{exception.Message.TrimEnd('.')} (invariant GRAPH-C4-4).");
        }

        if (maxLoopIterations is not null && retryTarget is null)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' declares a 'maxLoopIterations' but no 'retryTarget', so it has no fix loop to bound. "
                                                     + "The cap counts routes to a retry target, and this node routes none (invariant GRAPH-C4-4).");
        }

        return maxLoopIterations;
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
        // Which materializer owns each template node. A template subtree is cloned once per task by the node that owns
        // it, and the zero-task decomposition writes its no-op verdict row under the template's OWN key — so a node
        // two materializers both claim would be seeded twice under one key, and the second producer would fail on the
        // store's existing-key refusal on every tick from then on.
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

                // The join is where the clones hand their work back, so it has to FOLLOW the node that decomposes it.
                // Naming that node itself, or one of its ancestors, is the one materialization shape that reads as a
                // cycle: expansion wires every clone's leaf to the join, so the expanded graph would route the clones'
                // output back into the run that produced them — and the virtual template edge the invariants below walk
                // closes the same loop where EnsureAcyclic, which sees the AUTHORED edges only, cannot see it.
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
    ///     <c>GRAPH-C4-1</c> — every path reaches an end of the run.
    ///     <para>
    ///         Read literally the rule is implied by acyclicity, since a node with no out-edge IS an end. The
    ///         non-vacuous version is over the edges a run can TAKE, which is narrower: an out-edge of a human gate
    ///         that is false for all three answers can never fire, so the branch behind it is written but unreachable.
    ///         <c>{"path":"decision","op":"eq","value":"Approved"}</c> — the past participle — validates today and
    ///         silently kills the branch at run time, which is precisely what
    ///         <see cref="DevWorkflowCondition" /> refuses for a provably dead comparison elsewhere.
    ///     </para>
    ///     <para>
    ///         Three steps, in this order, because the order decides which complaint the operator gets. The dead edges
    ///         are found first; then the live subgraph is asked whether anything is stranded, which is the complaint
    ///         that names the real damage; then a dead edge that stranded nothing is reported on its own. Step two can
    ///         only fire once step one has found something — over the whole edge set, acyclicity already guarantees
    ///         every node reaches an end — and it is kept anyway, because it is the invariant asked for, it costs one
    ///         walk, and it is the guard if a later change prunes edges for some other reason.
    ///     </para>
    ///     <para>
    ///         Decidable for a human gate only. A <c>Gate</c> node's output document is whatever the node produced, so
    ///         no definition-time reading of its conditions can say which of them will fire. It is deliberately NOT the
    ///         rule that every gate ANSWER has somewhere to go: both seeded gates carry an <c>Approve</c> edge and
    ///         nothing else, and a rejection ending the run is X10 working as designed.
    ///     </para>
    /// </summary>
    private void EnsureEveryPathReachesAnEnd()
    {
        var dead = DeadGateEdges();
        if (dead.Count == 0)
        {
            return;
        }

        var reachesAnEnd = NodesThatReachAnEnd(dead);

        // The gates that own a dead edge come first, and only they get the sentence that says so. Chain two gates and
        // strand the downstream one, and every gate above it is stranded too — an ordinal tie-break would then name a
        // gate whose own edge is fine and send the operator to fix the wrong line.
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

    /// <summary>
    ///     <c>GRAPH-C4-2</c> — a node that writes outside its sandbox is reached through a human gate.
    ///     <para>
    ///         The structural half. Y3 (<see cref="EnsureAppliesAreGated" />) is kept exactly as it is and is strictly
    ///         stronger for an apply node — an IMMEDIATE gate predecessor carrying only the approval — so this rule
    ///         never weakens it; approval policy here is tighten-only. What this adds is the DECLARED case: an Agent
    ///         node whose author wrote <c>WriteExecute</c> into <c>requiredCapabilities</c> is taken at their word, and
    ///         a run must not be able to reach it without an operator having been asked.
    ///     </para>
    ///     <para>
    ///         The waiver is the graph's own <c>allowUngatedWrites</c>, which is a template saying so once and in
    ///         writing rather than each node quietly opting itself out.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     <c>GRAPH-C4-3</c> — an apply follows a validation.
    ///     <para>
    ///         The structural half: every path into a <c>toolMode: Apply</c> node passes a Tool node in
    ///         <c>Validate</c> mode. It is deliberately optimistic in one place, and this says so rather than claiming
    ///         an airtight proof — admission drops inbound edges whose source is a template key, so the
    ///         <c>validate(template) → join</c> edge that carries the property in the definition graph is not a
    ///         run-time dependency. The gap is closed operationally rather than structurally: the materialized graph
    ///         carries the clones' real validate edges, and the dispatcher re-asks the question over the rows a run
    ///         actually landed.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     "Has EVERY run that reaches this node already passed a node with property <paramref name="property" />?"
    ///     — one forward fixpoint in topological order, shared by both invariants above, because both ask that one
    ///     question and two implementations of it would drift.
    ///     <para>
    ///         One recurrence, no special case: <c>Assured(v) = P(v) || Combine(inbound of v)</c> with
    ///         <c>Combine(∅) = false</c>, so an entry node evaluates to <c>P(entry)</c> and is NOT initialised false.
    ///         Initialising it false erases the property on the entry node itself, which rejects two perfectly valid
    ///         shapes — a definition whose entry IS the gate guarding the write, and one whose entry is the validation
    ///         ahead of the gate and the apply. The inclusive reading is safe because nothing can be at once the
    ///         property and the thing checked: a gate carries no effects at all, and a Tool node is <c>Validate</c> or
    ///         <c>Apply</c>, never both.
    ///     </para>
    ///     <para>
    ///         <c>Combine</c> is keyed on the node's <c>joinPolicy</c> and never on its node TYPE: <b>OR</b> when
    ///         <c>All</c>, because every inbound branch must complete and one of them carrying the property is enough,
    ///         and <b>AND</b> when <c>Any</c>, because only one branch may have run. This is the runtime's own
    ///         semantics — admission reads <c>joinPolicy</c> for every node type and the parser defaults it to
    ///         <c>All</c> everywhere. Keying on <c>NodeType == Join</c> rejects the shipped template, whose
    ///         verification node is an AGENT with two inbound edges.
    ///     </para>
    /// </summary>
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
    ///     A topological order of the augmented graph — every node after all of its inbound sources — so one pass
    ///     computes the fixpoint. Safe because acyclicity is already proven before any of this runs.
    /// </summary>
    private List<string> AncestorsFirst(ILookup<string, DevWorkflowGraphEdge> inbound)
    {
        var order = new List<string>(Nodes.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeKey in Nodes.Keys)
        {
            Place(nodeKey);
        }

        return order;

        void Place(string nodeKey)
        {
            if (!placed.Add(nodeKey))
            {
                return;
            }

            foreach (var edge in inbound[nodeKey])
            {
                Place(edge.From);
            }

            order.Add(nodeKey);
        }
    }

    /// <summary>
    ///     The out-edges of a human gate that no answer would take, asked of the dispatcher's own routing rather than
    ///     re-derived by reading the condition — the same reason <see cref="EnsureCarriesOnlyTheApproval" /> asks it
    ///     that way, and the same drift it avoids.
    /// </summary>
    private HashSet<DevWorkflowGraphEdge> DeadGateEdges() =>
    [
        .. Edges.Where(edge => Nodes[edge.From].NodeType == DevWorkflowNodeType.HumanGate
                               && !DevWorkflowStateMachine.GateAnswers.Any(answer => DevWorkflowStateMachine.GateEdgeFires(edge, answer)))
    ];

    /// <summary>
    ///     Which nodes can still reach a member of <see cref="TerminalNodeKeys" /> once the dead edges are taken out —
    ///     walked backwards from the ends over the live edge set, which is one traversal rather than one per node.
    /// </summary>
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

    /// <summary>
    ///     The authored edges plus one VIRTUAL edge from each materializing node to its template root.
    ///     <para>
    ///         Not an invention: after expansion the materializer wires exactly that edge for every dependency-free
    ///         task, chains dependent clones off their dependency's leaves and wires each clone's leaf to the join. The
    ///         virtual edge is the definition-time image of a real run-time one, which is why the invariants below give
    ///         the same answer before and after materialization — and why they can be checked at save, when the author
    ///         is still there to fix what they say.
    ///     </para>
    ///     <para>
    ///         A node may name ITSELF as its own template root — <see cref="ValidateTemplateSubtree" /> exempts that
    ///         case from the nested-materialization refusal — so a self-edge is skipped. With it skipped the augmented
    ///         graph is acyclic, and that rests on two rules rather than on one: a template subtree's only exit is its
    ///         join, by construction, so the only way a template root could reach its materializing node is through
    ///         that join — and <see cref="Validate" /> refuses a join that IS the materializing node or one of its
    ///         ancestors. Without that second rule the virtual edge closes a loop over which
    ///         <see cref="EnsureAcyclic" />, which walks the authored edges only, is silent, and
    ///         <see cref="AncestorsFirst" /> would answer with an order that is not topological.
    ///     </para>
    /// </summary>
    private List<DevWorkflowGraphEdge> AugmentedEdges() =>
    [
        .. Edges,
        .. Nodes.Values.Where(static node => node.Materialization is not null)
                .Select(static node => new DevWorkflowGraphEdge(node.NodeKey, node.Materialization!.TemplateNodeKey, Condition: null))
                .Where(static edge => !string.Equals(edge.From, edge.To, StringComparison.Ordinal))
    ];

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
    private void ValidateTemplateSubtree(string nodeKey, DevWorkflowMaterialization materialization, Dictionary<string, string> templateOwner)
    {
        var subtree = TemplateSubtree(materialization);
        foreach (var key in subtree)
        {
            // Exactly one materializer owns each template node. Two that share a subtree stay structurally legal only
            // until a decomposition finds no work: each producer then seeds the SAME template key with its own no-op
            // verdict row under its own operation id, the first commits, and the second is refused by the store for
            // the life of the run — a deadlock authored at save time, so it is refused at save time.
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

            // GRAPH-C4-1, step four. TerminalNodeKeys is "every node with no out-edge", template keys included, and
            // the doc there calls that moot because a template never gets a node run. The zero-task decomposition's
            // no-op verdict row is the exception that falsifies the premise: a template leaf would take a Succeeded row
            // at a key the completion predicate reads, and the run would report Completed though its real tail never
            // ran. Refusing the shape at save is cheaper than teaching two runtime rules about each other.
            //
            // Scoped to the nodes that row is written FOR, which is what the premise it restores is about: the
            // materializer seeds one row per Tool/Validate node of the subtree and none for anything else, so an
            // edge-less DevTask or Agent template stays rowless and can neither satisfy nor block the completion
            // predicate — exactly as the doc says. Widening the rule to every node type would refuse a shape that has
            // always been legal and is still harmless, the baseline decomposition template among them.
            //
            // "Has an out-edge" implies "reaches the join" because the subtree pulls every non-join edge target back
            // into itself, so the only edge that can leave one is the edge to the declared join — an argument that
            // leans on acyclicity, which this call runs BEFORE EnsureAcyclic proves. Nothing unsound follows: a cyclic
            // graph is refused a few lines later either way, and it simply reads this complaint rather than the cycle
            // one.
            if (Nodes[key] is { NodeType: DevWorkflowNodeType.Tool, ToolMode: DevWorkflowToolMode.Validate } && OutboundEdges(key).Count == 0)
            {
                throw new DevWorkflowValidationException($"Node '{key}' validates inside the materialization template of '{nodeKey}' and no edge leaves it, so it "
                                                         + "would count as an end of the run — and a decomposition that found no work records a succeeded verdict "
                                                         + $"under that key. Give it an edge to the join node '{materialization.JoinNodeKey}' (invariant "
                                                         + "GRAPH-C4-1).");
            }
        }
    }

    /// <summary>
    ///     No cycle in the authored graph, and none in the AUGMENTED one either.
    ///     <para>
    ///         The second walk is not a duplicate of the first. Each materializer's own join is already refused as
    ///         itself or one of its ancestors, which closes the loop ONE virtual edge can make — but two materializers
    ///         whose templates lead into each other close a loop no single-materializer rule can see: authored the
    ///         graph is acyclic, and only the virtual edges together make it a cycle. That matters twice over.
    ///         <see cref="AncestorsFirst" /> would answer with an order that is not topological, so
    ///         <see cref="Assured" /> would compute a one-pass fixpoint over it and give an order-dependent answer;
    ///         and at run time the clone edges each expansion wires turn the virtual cycle into a real one.
    ///     </para>
    ///     <para>
    ///         Authored first, so a plain back edge still reads as the plain complaint. A graph with no materialization
    ///         has the same edge set twice and skips the second walk entirely.
    ///     </para>
    /// </summary>
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

    /// <summary>Depth-first colouring: white unvisited, grey on the current path, black finished. A grey hit is the cycle.</summary>
    private void EnsureAcyclic(IReadOnlyList<DevWorkflowGraphEdge> edges, Func<string, IReadOnlyList<string>, string> message)
    {
        var outbound = edges.ToLookup(static edge => edge.From, StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
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
                // The path from the repeated key onwards IS the cycle, which is what lets the message name the nodes
                // that made it rather than the one node the walk happened to come back to.
                throw new DevWorkflowValidationException(message(nodeKey, path[path.IndexOf(nodeKey)..]));
            }

            path.Add(nodeKey);
            foreach (var edge in outbound[nodeKey])
            {
                Walk(edge.To);
            }

            path.RemoveAt(path.Count - 1);
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

    /// <summary>
    ///     A boolean that is absent, null or <c>false</c> reads as false, and anything that is not a boolean at all is
    ///     refused rather than read as one. A graph-level waiver written as <c>"true"</c> must not silently be no
    ///     waiver, and it must not silently be one either.
    /// </summary>
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

    /// <summary>
    ///     A required enum member BY NAME. Never <c>Enum.TryParse</c> on its own: that accepts a numeric token, so
    ///     <c>"nodeType": "3"</c> would parse into a value no member has and reach the per-type config table as a
    ///     missing key rather than as the refusal an author can read.
    /// </summary>
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
