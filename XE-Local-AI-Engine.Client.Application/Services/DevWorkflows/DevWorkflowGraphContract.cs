namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>The API layer's questions about a stored graph, answered by the parser the dispatcher routes with.</summary>
/// <remarks>
///     The parsed graph itself stays internal: it is the runtime's projection, not a wire shape. What must not be
///     duplicated is the JUDGEMENT, because a second implementation of it would drift from the one that decides.
///     See docs/wiki/25-dev-workflows.md ("The application service seams").
/// </remarks>
public static class DevWorkflowGraphContract
{
    /// <summary>Validates a definition's graph at SAVE time and answers its node count.</summary>
    /// <remarks>
    ///     The count is the denormalized column the definition list reads instead of parsing. Throws
    ///     <see cref="DevWorkflowValidationException" /> for anything the dispatcher could not route; run start
    ///     validates again, because an agent definition can be deleted in between. The cap is READ here, the parser
    ///     staying option-free, but ENFORCED inside the parse: everything the parse does after counting is
    ///     proportional to the node count, so a cap applied to the finished graph would bound none of it.
    /// </remarks>
    public static int ValidateAndCountNodes(string graphJson, int maxNodes) =>
        DevWorkflowGraph.Parse(graphJson, maxNodes).Nodes.Count;

    /// <summary>A graph node's <c>toolMode</c> in the parser's own spelling, so what is STORED is canonical.</summary>
    /// <remarks>
    ///     A value the parser would reject is handed back untouched: refusing it is
    ///     <see cref="ValidateAndCountNodes" />'s job, and quietly rewriting it would hide the mistake. By NAME for the
    ///     same reason — <c>Enum.TryParse</c> takes a numeric token, so <c>"1"</c> would be REWRITTEN into
    ///     <c>Apply</c> and stored as a mode the author never wrote, past the refusal the parser is there to give.
    /// </remarks>
    public static string? CanonicalToolMode(string? toolMode) =>
        GraphWorkflowTokens.TryParseName<DevWorkflowToolMode>(toolMode, out var parsed) ? parsed.ToString() : toolMode;

    /// <summary>Which decisions a node run in <paramref name="status" /> can take.</summary>
    /// <remarks>
    ///     A gate's three answers and <c>Skip</c> from <c>WaitingForApproval</c>, the three interventions from
    ///     <c>Blocked</c>, and nothing at all from anywhere else. Asked of the state machine rather than listed again
    ///     here, so what is offered and what the decision endpoint accepts cannot drift — including the one transition
    ///     that is legal for the RUNTIME and not for a person, the fix loop's reset of an open gate.
    /// </remarks>
    public static IReadOnlyList<string> AllowedDecisions(DevWorkflowNodeRunStatus status) =>
    [
        .. Enum.GetValues<DevWorkflowDecisionKind>()
               .Where(decision => DevWorkflowStateMachine.IsDecidable(status, decision))
               .Select(static decision => decision.ToString())
    ];

    /// <summary>The nodes of every materialization template subtree — the ones a run deliberately gives no node run.</summary>
    /// <remarks>
    ///     The API needs the answer because an edge from a template is not something a node run can be waiting for:
    ///     nothing will ever have a row for its source. Answers EMPTY for a graph that cannot be parsed, which is also
    ///     the honest answer — a graph nothing can route declares no templates for anyone to be waiting on.
    ///     See docs/wiki/25-dev-workflows.md ("The application service seams").
    /// </remarks>
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

    /// <summary>What each node of a stored graph can change, as effect names — the editor's badge row.</summary>
    /// <remarks>
    ///     The author's REASON is not here: it stays on the wire node's own <c>requiredCapabilities</c>, the field it
    ///     was written into. Answers EMPTY for an unparseable graph, exactly as <see cref="TemplateNodeKeys" /> does.
    ///     See docs/wiki/25-dev-workflows.md ("The application service seams").
    /// </remarks>
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

    /// <summary>The attempt cap each node's DEFINITION declares, keyed by node key, with the parser's default applied.</summary>
    /// <remarks>
    ///     The default comes from the parser so that this and <c>DevWorkflowGraph</c> cannot disagree about a node that
    ///     omits <c>maxAttempts</c>. It is where a node run's <c>MaxAttempts</c> started, before any operator Retry
    ///     widened it in place, so the difference between the two is the record of the widening itself.
    ///     See docs/wiki/25-dev-workflows.md ("The application service seams").
    /// </remarks>
    public static IReadOnlyDictionary<string, int> DeclaredMaxAttempts(string graphJson)
    {
        try
        {
            return DevWorkflowGraph.Parse(graphJson)
                                   .Nodes.ToDictionary(static entry => entry.Key, static entry => entry.Value.MaxAttempts, StringComparer.Ordinal);
        }
        catch (DevWorkflowValidationException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>Which of a run's skipped node runs the state machine WAIVES, so the API sends the verdict.</summary>
    /// <remarks>
    ///     A skip an operator chose is excused and a downstream <c>All</c> join carries on past it; a skip that
    ///     cascaded off a Failed ancestor or a branch nothing routed down is dead and the join will skip. Status alone
    ///     cannot tell the two apart, and the deciding ancestor need not be near the join the client is drawing.
    ///     Answers <c>null</c> — unknown, not "none" — for a graph that cannot be routed, never a verdict about it.
    /// </remarks>
    public static IReadOnlySet<string>? WaivedSkipNodeKeys(string graphJson, IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        try
        {
            return DevWorkflowStateMachine.WaivedSkipNodeKeys(DevWorkflowGraph.Parse(graphJson), nodeRunsByKey);
        }
        catch (DevWorkflowValidationException)
        {
            return null;
        }
    }

    /// <summary>Whether a <c>Reject</c> at <paramref name="nodeKey" /> has somewhere to go.</summary>
    /// <remarks>
    ///     False means the rejection ends the run, and the confirm dialog can only say so because the server answered
    ///     this before the click. Answered by evaluating the gate's real out-edge conditions against the document the
    ///     gate would actually produce, so an unconditional out-edge counts: it accepts every answer, this one too.
    /// </remarks>
    public static bool HasRejectBranch(string graphJson, string nodeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);

        var graph = DevWorkflowGraph.Parse(graphJson);
        return graph.OutboundEdges(nodeKey).Any(static edge => DevWorkflowStateMachine.GateEdgeFires(edge, DevWorkflowDecisionKind.Reject));
    }

    /// <summary>Whether a node run's output says it validated nothing because there was nothing to validate.</summary>
    /// <remarks>
    ///     The verdict the zero-task decomposition seeds onto its template's checks (ruling D12). Asked here so the API and
    ///     the runtime read ONE spelling of the token. The row is a real <c>Succeeded</c> row and has to be, or the
    ///     join behind it would never let the apply through — but it stands for work that did not happen, so every
    ///     count and badge that says "done" must tell the two apart. An unreadable document is not this verdict.
    /// </remarks>
    public static bool ValidationWasNotApplicable(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("verdict", out var verdict)
                   && verdict.ValueKind == JsonValueKind.String
                   && string.Equals(verdict.GetString(), DevWorkflowNodeOutputVerdicts.ValidationNotApplicable, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
