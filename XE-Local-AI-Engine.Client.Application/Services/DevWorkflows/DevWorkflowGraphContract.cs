namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The three questions the API layer asks about a stored graph, answered by the SAME parser, condition evaluator
///     and transition table the dispatcher routes with.
///     <para>
///         The parsed graph itself stays internal on purpose: it is the runtime's projection, not a wire shape, and the
///         API composes its own field-for-field mirror from the JSON. What must not be duplicated is the JUDGEMENT —
///         whether a graph is routable, which answers a node run can take, and where a rejection would go — because a
///         second implementation of any of those would drift from the one that actually decides.
///     </para>
/// </summary>
public static class DevWorkflowGraphContract
{
    /// <summary>
    ///     Validates a definition's graph at SAVE time and answers its node count, which is the denormalized column the
    ///     definition list reads instead of parsing. Throws <see cref="DevWorkflowValidationException" /> for anything
    ///     the dispatcher could not route: bad JSON, an unknown node type, a cycle, an orphan, a template with edges.
    ///     <para>
    ///         Run start validates again rather than trusting this, because an agent definition can be deleted between
    ///         the save and the start.
    ///     </para>
    /// </summary>
    public static int ValidateAndCountNodes(string graphJson) =>
        DevWorkflowGraph.Parse(graphJson).Nodes.Count;

    /// <summary>
    ///     A graph node's <c>toolMode</c> in the parser's own spelling, so what is STORED is canonical whatever casing
    ///     an author sent. A value the parser would reject is handed back untouched — refusing it is
    ///     <see cref="ValidateAndCountNodes" />'s job, and quietly rewriting it would hide the mistake.
    /// </summary>
    public static string? CanonicalToolMode(string? toolMode) =>
        Enum.TryParse<DevWorkflowToolMode>(toolMode, ignoreCase: true, out var parsed) ? parsed.ToString() : toolMode;

    /// <summary>
    ///     Which decisions a node run in <paramref name="status" /> can take: a gate's three answers and <c>Skip</c>
    ///     from <c>WaitingForApproval</c>, the three interventions from <c>Blocked</c>, and nothing at all from
    ///     anywhere else.
    ///     <para>
    ///         Asked of the state machine rather than listed again here, so what is offered and what the decision
    ///         endpoint accepts cannot drift — including the one transition that is legal for the RUNTIME and not for a
    ///         person, the fix loop's reset of an open gate.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> AllowedDecisions(DevWorkflowNodeRunStatus status) =>
    [
        .. Enum.GetValues<DevWorkflowDecisionKind>()
               .Where(decision => DevWorkflowStateMachine.IsDecidable(status, decision))
               .Select(static decision => decision.ToString())
    ];

    /// <summary>
    ///     The nodes of every materialization template subtree — the ones a run deliberately gives no node run to.
    ///     <para>
    ///         Asked of the parser rather than re-derived from the wire graph, which is the reason this class exists: a
    ///         second walk of the same document drifts from the one the dispatcher admits by. The API needs the answer
    ///         because an edge from a template is not something a node run can be waiting for — nothing will ever have
    ///         a row for its source.
    ///     </para>
    ///     <para>
    ///         Answers EMPTY for a graph that cannot be parsed, rather than throwing like its siblings here. This is a
    ///         read path, and a run whose pinned graph is unroutable is exactly the run an operator most needs to open:
    ///         it has already been failed with the reason on the row, and refusing to render it would hide that reason
    ///         behind a 500. Empty is the honest answer as well — a graph nothing can route declares no templates for
    ///         anyone to be waiting on — and it degrades to the behaviour that shipped before materialization existed.
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> TemplateNodeKeys(string graphJson)
    {
        try
        {
            return DevWorkflowGraph.Parse(graphJson).TemplateKeys;
        }
        catch (DevWorkflowValidationException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     What each node of a stored graph can change, as effect names — the editor's badge row.
    ///     <para>
    ///         Asked of the parser rather than re-derived from the wire document, for the reason this class exists: the
    ///         invariants refuse a save on these effects, so an editor computing its own set would show badges that
    ///         disagree with the 400 the operator gets. The author's REASON is not here — it stays on the wire node's
    ///         own <c>requiredCapabilities</c>, which is the field it was written into.
    ///     </para>
    ///     <para>
    ///         Answers EMPTY for a graph that cannot be parsed, exactly as <see cref="TemplateNodeKeys" /> does and for
    ///         the same reason: this is a read path, and a run whose pinned graph is unroutable is the one an operator
    ///         most needs to be able to open.
    ///     </para>
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EffectsOf(string graphJson)
    {
        try
        {
            return DevWorkflowGraph.Parse(graphJson)
                                   .Nodes
                                   .ToDictionary(static node => node.Key,
                                       static IReadOnlyList<string> (node) =>
                                           [.. DevWorkflowGraph.Effects(node.Value).Select(static effect => effect.ToString()).Order(StringComparer.Ordinal)],
                                       StringComparer.Ordinal);
        }
        catch (DevWorkflowValidationException)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     Whether a <c>Reject</c> at <paramref name="nodeKey" /> has somewhere to go. False means X10: the rejection
    ///     ends the run, and the confirm dialog can only say so because the server answered this before the click.
    ///     <para>
    ///         Answered by evaluating the gate's real out-edge conditions against the document the gate would actually
    ///         produce, so an unconditional out-edge counts — it accepts every answer, including this one.
    ///     </para>
    /// </summary>
    public static bool HasRejectBranch(string graphJson, string nodeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);

        var graph = DevWorkflowGraph.Parse(graphJson);
        return graph.OutboundEdges(nodeKey).Any(static edge => DevWorkflowStateMachine.GateEdgeFires(edge, DevWorkflowDecisionKind.Reject));
    }
}
