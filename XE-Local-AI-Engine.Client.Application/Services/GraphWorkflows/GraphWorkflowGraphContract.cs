namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The questions the API layer asks about a stored graph, answered by the SAME parser, condition evaluator and
///     transition table the dispatcher routes with.
///     <para>
///         The parsed graph itself stays internal on purpose: it is the runtime's projection, not a wire shape, and the
///         API composes its own field-for-field mirror from the JSON. What must not be duplicated is the JUDGEMENT —
///         whether a graph is routable, which answers a node run can take, where a rejection would go, and which tools
///         a definition would run — because a second implementation of any of those would drift from the one that
///         actually decides.
///     </para>
/// </summary>
public static class GraphWorkflowGraphContract
{
    /// <summary>
    ///     Validates a definition's graph at SAVE time and answers its node count, which is the denormalized column the
    ///     definition list reads instead of parsing. Throws <see cref="GraphWorkflowValidationException" /> for
    ///     anything the dispatcher could not route, and for a graph over the configured node cap.
    ///     <para>
    ///         The cap lives here rather than in the parser because it is an option: the parser stays option-free and
    ///         testable without a container, and this is the one place a save and a run both come through.
    ///     </para>
    /// </summary>
    public static int ValidateAndCountNodes(string graphJson, int maxNodes)
    {
        var count = GraphWorkflowGraph.Parse(graphJson).Nodes.Count;
        return count <= maxNodes
            ? count
            : throw new GraphWorkflowValidationException($"The graph declares {count} nodes, more than the {maxNodes} one definition may carry.");
    }

    /// <summary>
    ///     Which decisions a node run in <paramref name="status" /> can take: a pause's two answers from
    ///     <c>WaitingForApproval</c>, and nothing at all from anywhere else.
    ///     <para>
    ///         Asked of the state machine rather than listed again here, so what the panel offers and what the decide
    ///         endpoint accepts cannot drift — a status that is not decidable at all must advertise NOTHING, or every
    ///         button it draws answers "conflict".
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> AllowedDecisions(GraphWorkflowNodeRunStatus status) =>
    [
        .. Enum.GetValues<GraphWorkflowDecisionKind>()
               .Where(decision => GraphWorkflowStateMachine.IsDecidable(status, decision))
               .Select(static decision => decision.ToString())
    ];

    /// <summary>
    ///     Whether a <c>Reject</c> at <paramref name="nodeKey" /> has somewhere to go, so a confirm dialog can say in
    ///     advance that the rejection ends the run.
    ///     <para>
    ///         Answered by evaluating the node's real out-edge conditions against the document a pause would actually
    ///         produce, so an unconditional out-edge counts — it accepts every answer, this one included.
    ///     </para>
    /// </summary>
    public static bool HasRejectBranch(string graphJson, string nodeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);

        var graph = GraphWorkflowGraph.Parse(graphJson);
        return graph.OutboundEdges(nodeKey).Any(static edge => GraphWorkflowStateMachine.DecisionEdgeFires(edge, GraphWorkflowDecisionKind.Reject));
    }

    /// <summary>
    ///     Every distinct tool name a <c>Tool</c> node in this graph would run. The parser cannot reach the tool
    ///     catalog, so the gate that refuses anything outside the read-local, no-approval envelope asks over this
    ///     rather than walking the document a second time.
    /// </summary>
    public static IReadOnlyList<string> ToolNodeNames(string graphJson) =>
        GraphWorkflowGraph.Parse(graphJson).ToolNodeNames;
}
