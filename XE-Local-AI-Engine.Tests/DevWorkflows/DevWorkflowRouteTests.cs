namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     T2: the route a settled node run records is the state machine's own verdict per edge, never a second copy of the
///     rule. The only equality the runtime guarantees is agreement with <c>EdgeState</c>; agreement with
///     <c>Admission</c> is false by construction, because admission is a question about a TARGET's inbound edges.
/// </summary>
public sealed class DevWorkflowRouteTests
{
    /// <summary>The four statuses a route may be taken from. The other five are refused — see the unreachability test.</summary>
    private static readonly DevWorkflowNodeRunStatus[] TerminalStatuses =
    [
        DevWorkflowNodeRunStatus.Succeeded,
        DevWorkflowNodeRunStatus.Failed,
        DevWorkflowNodeRunStatus.Skipped,
        DevWorkflowNodeRunStatus.Cancelled
    ];

    /// <summary>
    ///     Every fixture graph, every node, every terminal status: each out-edge lands in the half its own
    ///     <c>EdgeState</c> names, and nothing else lands anywhere.
    /// </summary>
    [Test]
    public void RouteTaken_AgreesWithEdgeState()
    {
        var checkedEdges = 0;
        foreach (var (fixtureName, graphJson) in FixtureGraphs())
        {
            var graph = DevWorkflowGraph.Parse(graphJson);
            foreach (var node in graph.Nodes.Values)
            {
                foreach (var status in TerminalStatuses)
                {
                    // The gate document is the one output any fixture edge has a condition over, so it is what makes
                    // the Satisfied half non-empty rather than trivially agreeing on an all-dead route.
                    var source = NodeRun(node.NodeKey, status, """{"status":"Succeeded","decision":"Approve"}""", node.NodeType);
                    var route = DevWorkflowStateMachine.RouteTaken(graph, source, decision: null);
                    var expected = graph.TemplateKeys.Contains(node.NodeKey) ? [] : graph.OutboundEdges(node.NodeKey);

                    var where = $"{fixtureName}/{node.NodeKey}/{status}";
                    AssertEx.Equal(expected.Count,
                        route.Satisfied.Count + route.Dead.Count,
                        $"{where}: every surviving out-edge is judged exactly once, and a template's are dropped.");
                    AssertEx.False(route.Truncated, $"{where}: no fixture fans out past the eight-key bound.");

                    foreach (var edge in expected)
                    {
                        var state = DevWorkflowStateMachine.EdgeState(edge, source);
                        var satisfied = state == DevWorkflowEdgeState.Satisfied;
                        AssertEx.Equal(satisfied,
                            route.Satisfied.Contains(edge.To, StringComparer.Ordinal),
                            $"{where} → '{edge.To}': the route must agree with EdgeState, which answered {state}.");
                        AssertEx.Equal(!satisfied,
                            route.Dead.Contains(edge.To, StringComparer.Ordinal),
                            $"{where} → '{edge.To}': the route must agree with EdgeState, which answered {state}.");
                        checkedEdges++;
                    }

                    if (status != DevWorkflowNodeRunStatus.Succeeded)
                    {
                        AssertEx.Empty(route.Satisfied,
                            $"{where}: a source that settled without succeeding kills every out-edge — it routed nowhere.");
                    }
                }
            }
        }

        AssertEx.True(checkedEdges > 0, "A fixture sweep that judged no edge would pass vacuously.");
    }

    /// <summary>
    ///     An edge leaving a materialization TEMPLATE is not a route, for the same reason <c>Admission</c> drops it: the
    ///     template is the one node deliberately never instantiated, and its edges are the shape the clones' own edges
    ///     stand in for.
    /// </summary>
    [Test]
    public void RouteTaken_DropsEdgesLeavingAMaterializationTemplate()
    {
        // The subtree fixture is the one whose template has an out-edge of its own — implement → validate, both inside
        // the cloned subtree — so dropping it is observable rather than vacuous.
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.DecompositionSubtree);
        var template = graph.TemplateKeys.First(key => graph.OutboundEdges(key).Count > 0);

        var route = DevWorkflowStateMachine.RouteTaken(graph, NodeRun(template, DevWorkflowNodeRunStatus.Succeeded), decision: null);

        AssertEx.Empty(route.Satisfied, "A template routes nowhere.");
        AssertEx.Empty(route.Dead, "A template's out-edges are dropped, not killed — nothing was ever waiting on them.");
    }

    /// <summary>
    ///     N1's unreachability assertion: <c>DevWorkflowRoute</c> has no <c>Pending</c> bucket, and the reason is that a
    ///     non-terminal source is refused outright rather than recorded as a silently empty document.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowNodeRunStatus.Pending)]
    [Arguments(DevWorkflowNodeRunStatus.Queued)]
    [Arguments(DevWorkflowNodeRunStatus.Running)]
    [Arguments(DevWorkflowNodeRunStatus.WaitingForApproval)]
    [Arguments(DevWorkflowNodeRunStatus.Blocked)]
    public void RouteTaken_RefusesANonTerminalSource(DevWorkflowNodeRunStatus status)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        _ = AssertEx.Throws<ArgumentException>(() => DevWorkflowStateMachine.RouteTaken(graph, NodeRun("research", status), decision: null),
            $"EdgeState answers Pending for a {status} source, and the route document cannot express that.");
    }

    /// <summary>
    ///     On a human gate the route's verdicts and <c>GateEdgeFires</c> are the same question asked twice — the gate's
    ///     out-edges are evaluated against the document this class itself wrote for that answer. The answer token rides
    ///     along so a reader never has to re-derive it from the edges.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowDecisionKind.Approve)]
    [Arguments(DevWorkflowDecisionKind.RequestChanges)]
    public void RouteTaken_OnAHumanGate_AgreesWithGateEdgeFires(DevWorkflowDecisionKind decision)
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var source = NodeRun("approve",
            DevWorkflowNodeRunStatus.Succeeded,
            DevWorkflowStateMachine.GateOutputJson(decision),
            DevWorkflowNodeType.HumanGate);

        var route = DevWorkflowStateMachine.RouteTaken(graph, source, decision);

        AssertEx.Equal(decision.ToString(), route.GateAnswer, "The answer is recorded as its own token, not left to be inferred.");
        foreach (var edge in graph.OutboundEdges("approve"))
        {
            AssertEx.Equal(DevWorkflowStateMachine.GateEdgeFires(edge, decision),
                route.Satisfied.Contains(edge.To, StringComparer.Ordinal),
                $"'{edge.To}' must be routed exactly when the gate's own edge rule says the answer reaches it.");
        }
    }

    /// <summary>
    ///     The gate answer is read back off the document that wrote it, so the writer and the reader cannot drift; any
    ///     other document — a structural node's, or nothing at all — has no answer to give.
    /// </summary>
    [Test]
    public void GateDecisionFrom_ReadsBackWhatGateOutputJsonWrote()
    {
        foreach (var decision in DevWorkflowStateMachine.GateAnswers)
        {
            AssertEx.Equal(decision,
                DevWorkflowStateMachine.GateDecisionFrom(DevWorkflowStateMachine.GateOutputJson(decision)),
                "The gate's own output document is where its answer is recorded.");
        }

        AssertEx.Null(DevWorkflowStateMachine.GateDecisionFrom(null), "No document, no answer.");
        AssertEx.Null(DevWorkflowStateMachine.GateDecisionFrom("{ not json"), "An unreadable document is absence, not an exception.");
        AssertEx.Null(DevWorkflowStateMachine.GateDecisionFrom("""{"status":"Succeeded","attempt":1}"""), "An inline node's output carries no answer.");
        AssertEx.Null(DevWorkflowStateMachine.GateDecisionFrom("""{"decision":"Ponder"}"""), "A token outside the vocabulary is not an answer.");
    }

    /// <summary>
    ///     A fan-out wider than the record's bound is capped at eight keys per half and flagged, and the serialized
    ///     document is trimmed until it fits the column rather than clipped mid-string — a document that no longer
    ///     parses would take the whole measurement recipe down with it.
    /// </summary>
    [Test]
    public void RouteJson_CapsTheKeysAndFitsTheColumnBound()
    {
        var graph = DevWorkflowGraph.Parse(WideFanOut(successors: 12, keyLength: 100));
        var route = DevWorkflowStateMachine.RouteTaken(graph, NodeRun("start", DevWorkflowNodeRunStatus.Succeeded, nodeType: DevWorkflowNodeType.Parallel), decision: null);

        AssertEx.True(route.Truncated, "Twelve successors do not fit eight slots, and a short list must never read as a complete one.");
        AssertEx.Equal(expected: 8, route.Dead.Count + route.Satisfied.Count, "Each half is capped at eight keys.");

        var json = DevWorkflowStateMachine.RouteJson(route);
        AssertEx.True(json.Length <= 1024, $"route_json is bounded at 1024 characters; this one was {json.Length}.");

        using var document = JsonDocument.Parse(json);
        AssertEx.True(document.RootElement.GetProperty("truncated").GetBoolean(), "The trimmed document has to say so.");
        AssertEx.True(document.RootElement.TryGetProperty("satisfied", out _), "The document keeps its shape after trimming.");
        AssertEx.True(document.RootElement.TryGetProperty("dead", out _), "The document keeps its shape after trimming.");
    }

    /// <summary>A route that fits is serialized whole, in the shape the runbook's queries read.</summary>
    [Test]
    public void RouteJson_OnAnOrdinaryRoute_IsTheDocumentedShape()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ApprovalBranches);
        var route = DevWorkflowStateMachine.RouteTaken(graph,
            NodeRun("approve", DevWorkflowNodeRunStatus.Succeeded, DevWorkflowStateMachine.GateOutputJson(DevWorkflowDecisionKind.Approve), DevWorkflowNodeType.HumanGate),
            DevWorkflowDecisionKind.Approve);

        AssertEx.Equal("""{"satisfied":["ship"],"dead":["revise"],"gateAnswer":"Approve","truncated":false}""",
            DevWorkflowStateMachine.RouteJson(route));
    }

    /// <summary>Every graph fixture the runtime suites route over, by name, so a new one joins the sweep for free.</summary>
    private static IEnumerable<(string Name, string GraphJson)> FixtureGraphs() =>
        typeof(DevWorkflowGraphs).GetFields(BindingFlags.Public | BindingFlags.Static)
                                 .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                                 .Select(field => (field.Name, (string)field.GetRawConstantValue()!));

    /// <summary>One source with <paramref name="successors" /> long-keyed leaves — the shape the route's bounds exist for.</summary>
    private static string WideFanOut(int successors, int keyLength)
    {
        var keys = Enumerable.Range(1, successors)
                             .Select(index => "leaf" + index.ToString(CultureInfo.InvariantCulture) + new string('x', keyLength))
                             .ToList();
        var nodes = string.Join(",", keys.Select(key => $$"""{"nodeKey":"{{key}}","nodeType":"Agent"}"""));
        var edges = string.Join(",", keys.Select(key => $$"""{"from":"start","to":"{{key}}"}"""));
        return $$"""{"schemaVersion":1,"nodes":[{"nodeKey":"start","nodeType":"Parallel"},{{nodes}}],"edges":[{{edges}}]}""";
    }

    private static DevWorkflowNodeRunSnapshot NodeRun(string nodeKey,
        DevWorkflowNodeRunStatus status,
        string? outputJson = null,
        DevWorkflowNodeType nodeType = DevWorkflowNodeType.Agent) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            nodeKey,
            nodeType,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            status,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 1,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            outputJson,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: null,
            EndedAtUtc: null,
            CreatedAtUtc: 0);
}
