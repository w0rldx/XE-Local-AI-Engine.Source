namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
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
        using var output = JsonDocument.Parse(DevWorkflowStateMachine.GateOutputJson(DevWorkflowDecisionKind.Reject));
        return graph.OutboundEdges(nodeKey).Any(edge => DevWorkflowCondition.Evaluate(edge.Condition, output.RootElement));
    }
}
