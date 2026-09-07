namespace XE_Local_AI_Engine.Tests.GraphWorkflows.Import;

using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pure half of the one-shot Open Canvas import: a stored canvas graph in, a Graph Workflow document out. No
///     database and no host.
///     <para>
///         Totality is the property these tests exist for. The importer runs once, in the same build that drops
///         <c>canvas_workflows</c>, so a graph the mapper refused would be a graph destroyed — every shape has to come
///         out as a document, and whatever could not be carried across says so in <c>Reasons</c> instead.
///     </para>
/// </summary>
public sealed class CanvasWorkflowImportMapperTests
{
    /// <summary>
    ///     Every shape the canvas validator accepted, and several it did not. None of them may throw and none may
    ///     answer nothing: what the mapper cannot translate travels as a reason, never as a refusal.
    /// </summary>
    [Test]
    [Arguments(CanvasGraphs.Linear)]
    [Arguments(CanvasGraphs.WithDebug)]
    [Arguments(CanvasGraphs.WithChainedDebug)]
    [Arguments(CanvasGraphs.WithPause)]
    [Arguments(CanvasGraphs.WithModelProfile)]
    [Arguments(CanvasGraphs.AwkwardIds)]
    [Arguments(CanvasGraphs.DuplicateIds)]
    [Arguments(CanvasGraphs.DebugWithNoSuccessor)]
    [Arguments(CanvasGraphs.UnknownKind)]
    [Arguments(CanvasGraphs.BareFields)]
    [Arguments(CanvasGraphs.DanglingEdge)]
    [Arguments(CanvasGraphs.NotAGraph)]
    [Arguments(CanvasGraphs.Empty)]
    [Arguments(CanvasGraphs.NodesNotAnArray)]
    public void MapGraph_OverEveryStoredShape_AlwaysAnswersADocument(string canvasGraph)
    {
        var mapped = CanvasWorkflowImport.MapGraph(canvasGraph);

        _ = AssertEx.NotNull(mapped.Document, "the mapper never refuses a graph; a refusal here would be a canvas destroyed.");
        AssertEx.Equal(expected: 1, mapped.Document["schemaVersion"]!.GetValue<int>());
        _ = AssertEx.NotNull(mapped.Document["nodes"] as JsonArray, "a document always carries a nodes array, even an empty one.");
        _ = AssertEx.NotNull(mapped.Document["edges"] as JsonArray);
    }

    /// <summary>
    ///     A canvas graph that came across whole reports no reasons and, more importantly, parses through the SAME
    ///     validator the save endpoint runs — which is what makes it a definition an operator can start.
    /// </summary>
    [Test]
    public void MapGraph_OverALinearCanvas_ProducesADefinitionTheRealParserAccepts()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.Linear);
        var graphJson = mapped.Document.ToJsonString();

        AssertEx.Empty(mapped.Reasons, "nothing in a plain Start -> Agent -> End chain is lost in translation.");
        AssertEx.Equal(expected: 3, GraphWorkflowGraphContract.ValidateAndCountNodes(graphJson, maxNodes: 200),
            "the mapped document is held to the runtime's own parser, not to a second opinion.");

        AssertEx.Equal("start, agent-1, end", Keys(mapped.Document, "nodes"), "a canvas id already inside the key charset travels unchanged.");
        AssertEx.Equal("Start, Agent, End", string.Join(", ", Nodes(mapped.Document).Select(static node => node!["kind"]!.GetValue<string>())));

        var agent = Node(mapped.Document, "agent-1");
        AssertEx.Equal("Summarize", agent["label"]!.GetValue<string>());
        AssertEx.Equal(expected: 1, agent["maxAttempts"]!.GetValue<int>(), "an import is conservative: one try, not the three a hand-authored Agent node takes.");
        AssertEx.Equal("Summarize it.", agent["config"]!["instructions"]!.GetValue<string>());
        AssertEx.Equal("qwen3:8b", agent["config"]!["model"]!.GetValue<string>());
        AssertEx.True(agent["config"]!["includeUpstreamOutputs"]!.GetValue<bool>());
    }

    /// <summary>The graph-level seed text has exactly one destination: the Start node's default input.</summary>
    [Test]
    public void MapGraph_CarriesTheCanvasStartTextOntoTheStartNodesDefaultInput()
    {
        AssertEx.Equal("Summarize the release notes.",
            Node(CanvasWorkflowImport.MapGraph(CanvasGraphs.Linear).Document, "start")["config"]!["defaultInput"]!.GetValue<string>());

        AssertEx.Null(Node(CanvasWorkflowImport.MapGraph(CanvasGraphs.BareFields).Document, "start")["config"]!["defaultInput"],
            "an empty seed text is no seed text: a Start node that defaults the input to \"\" is not the same thing.");
    }

    /// <summary>
    ///     A Debug node was a side-event tap that forwarded its input unchanged, so eliding it and rewiring the edge
    ///     around it preserves the run's meaning exactly. Anything less faithful would be a behaviour change smuggled
    ///     into a data migration.
    /// </summary>
    [Test]
    public void MapGraph_ElidesDebugNodesAndRewiresTheEdgeAroundThem()
    {
        var single = CanvasWorkflowImport.MapGraph(CanvasGraphs.WithDebug);
        AssertEx.Equal("start, agent-1, agent-2, end", Keys(single.Document, "nodes"), "the Debug node has no destination here and is dropped.");
        AssertEx.Equal("start->agent-1, agent-1->agent-2, agent-2->end", Wiring(single.Document), "X -> Debug -> Y collapses to X -> Y.");
        AssertEx.Empty(single.Reasons, "an elided tap that had a successor lost nothing.");
        _ = GraphWorkflowGraphContract.ValidateAndCountNodes(single.Document.ToJsonString(), maxNodes: 200);

        var chained = CanvasWorkflowImport.MapGraph(CanvasGraphs.WithChainedDebug);
        AssertEx.Equal("start->agent-1, agent-1->end", Wiring(chained.Document), "two taps back to back collapse just as one does.");
    }

    /// <summary>
    ///     A Debug node with nothing after it leaves the path into it ending nowhere. The graph still travels — the
    ///     validator refuses it downstream and the definition lands needing attention rather than being discarded.
    /// </summary>
    [Test]
    public void MapGraph_WithADebugNodeThatHasNoSuccessor_KeepsTheGraphAndSaysWhatItLost()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.DebugWithNoSuccessor);

        AssertEx.Equal("start->agent-1", Wiring(mapped.Document), "the edge into the tap had nowhere to be rewired to.");
        AssertEx.Contains(string.Join(" ", mapped.Reasons), "had nothing after it");
        _ = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200),
            "which is the point: the row is preserved as a definition that cannot run until it is edited.");
    }

    /// <summary>
    ///     The canvas's Pause was a resume, not a decision, so <c>Approve</c> alone is the faithful translation — and
    ///     one allowed decision needs one matching out-edge, or the parser's pre-flight rule refuses the graph.
    /// </summary>
    [Test]
    public void MapGraph_GivesAPauseOneApproveDecisionAndAnEdgeThatFiresOnIt()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.WithPause);
        var pause = Node(mapped.Document, "pause-1");

        AssertEx.Equal("Sign off", pause["label"]!.GetValue<string>());
        AssertEx.Equal("Approve and continue?", pause["config"]!["prompt"]!.GetValue<string>());
        AssertEx.Equal("Approve", string.Join(", ", (pause["config"]!["allowedDecisions"] as JsonArray)!.Select(static entry => entry!.GetValue<string>())));
        AssertEx.False(pause["config"]!["requireComment"]!.GetValue<bool>());

        var outbound = AssertEx.NotNull(Edges(mapped.Document).Single(static edge => edge!["from"]!.GetValue<string>() == "pause-1"));
        AssertEx.Equal("approved", outbound["label"]!.GetValue<string>());
        AssertEx.Equal("output.decision", outbound["condition"]!["path"]!.GetValue<string>());
        AssertEx.Equal("Eq", outbound["condition"]!["op"]!.GetValue<string>(), "the wire member is 'op' with the short token, not 'operator'.");
        AssertEx.Equal("Approve", outbound["condition"]!["value"]!.GetValue<string>());

        _ = GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200);
        AssertEx.False(GraphWorkflowGraphContract.HasRejectBranch(mapped.Document.ToJsonString(), "pause-1"),
            "only the answer the canvas could give has anywhere to go.");
    }

    /// <summary>
    ///     The provider hint has no member in the new Agent config. It is dropped with a reason naming the node — never
    ///     the value, which is authored content and does not belong in a log or a description.
    /// </summary>
    [Test]
    public void MapGraph_DropsTheModelProfileAndNamesTheNodeRatherThanTheValue()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.WithModelProfile);

        AssertEx.Contains(string.Join(" ", mapped.Reasons), "model profile on node 'agent-1'");
        AssertEx.False(string.Join(" ", mapped.Reasons).Contains("reasoning-heavy", StringComparison.Ordinal),
            "a reason names the node it is about and never quotes what the operator wrote.");
        AssertEx.False(mapped.Document.ToJsonString().Contains("modelProfile", StringComparison.Ordinal), "and the member itself does not survive.");
        _ = GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200);
    }

    /// <summary>
    ///     Imported graphs arrive position-less by design: the editor already lays out a node without one on first
    ///     open, and a second layout rule here would leave one of the two dead.
    /// </summary>
    [Test]
    [Arguments(CanvasGraphs.Linear)]
    [Arguments(CanvasGraphs.WithPause)]
    [Arguments(CanvasGraphs.AwkwardIds)]
    public void MapGraph_EmitsNoPositionOnAnyNode(string canvasGraph) =>
        AssertEx.Empty(Nodes(CanvasWorkflowImport.MapGraph(canvasGraph).Document).Where(static node => node!["position"] is not null),
            "the importer draws nothing; the editor lays an imported graph out on first open.");

    /// <summary>
    ///     Node and edge keys share ONE namespace and one charset. An id the charset cannot take is replaced rather
    ///     than mangled into a collision, and an id that sanitizes to nothing still gets a key of its own.
    /// </summary>
    [Test]
    public void MapGraph_MintsKeysInsideTheCharsetAndNeverRepeatsOne()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.AwkwardIds);
        var keys = Nodes(mapped.Document).Select(static node => node!["key"]!.GetValue<string>())
                                         .Concat(Edges(mapped.Document).Select(static edge => edge!["key"]!.GetValue<string>()))
                                         .ToList();

        AssertEx.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count(), "one namespace: a repeated key makes an element lookup ambiguous.");
        foreach (var key in keys)
        {
            AssertEx.True(key.Length is > 0 and <= 64 && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'),
                $"key '{key}' is outside the charset a node_key column, an element id and a URL all have to survive.");
        }

        AssertEx.Equal("thestart, agentonedraft, n2", string.Join(", ", Nodes(mapped.Document).Select(static node => node!["key"]!.GetValue<string>())),
            "an id that sanitizes to nothing falls back to its index.");
        _ = GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200);
    }

    /// <summary>
    ///     Two canvas nodes sharing an id is damage the canvas validator refused, so nothing downstream can be trusted
    ///     to have prevented it. Both nodes survive with keys of their own, and the reason says which one the edges
    ///     went to.
    /// </summary>
    [Test]
    public void MapGraph_WithTwoNodesSharingOneId_KeepsBothAndSaysWhereTheEdgesWent()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.DuplicateIds);

        AssertEx.Equal("start, agent-1, n2, end", Keys(mapped.Document, "nodes"), "the second claimant takes an index key rather than overwriting the first.");
        AssertEx.Contains(string.Join(" ", mapped.Reasons), "Two canvas nodes share one id");
        AssertEx.Equal("start->agent-1, agent-1->end", Wiring(mapped.Document), "an id names one node, and it is the one that claimed it first.");
    }

    /// <summary>
    ///     A kind this runtime does not offer is written through as it stands. Guessing at it would invent a workflow
    ///     nobody authored, and dropping it would lose the node — so the parser refuses it and an operator sees what
    ///     the canvas actually held.
    /// </summary>
    [Test]
    public void MapGraph_WithAKindThisRuntimeDoesNotOffer_KeepsTheNodeAndSaysSo()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.UnknownKind);

        AssertEx.Equal("Switch", Node(mapped.Document, "switch-1")["kind"]!.GetValue<string>());
        AssertEx.Contains(string.Join(" ", mapped.Reasons), "canvas kind this runtime does not offer");

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200));
        AssertEx.Contains(refusal.Message, "'kind'");
    }

    /// <summary>
    ///     An edge naming a node the canvas never declared is dropped rather than emitted, because the parser treats an
    ///     unknown endpoint as structural and stops reading the whole graph at it.
    /// </summary>
    [Test]
    public void MapGraph_DropsAnEdgeThatNamesANodeTheCanvasNeverDeclared()
    {
        var mapped = CanvasWorkflowImport.MapGraph(CanvasGraphs.DanglingEdge);

        AssertEx.Equal("start->agent-1, agent-1->end", Wiring(mapped.Document));
        AssertEx.Contains(string.Join(" ", mapped.Reasons), "named a node the canvas does not declare");
        _ = GraphWorkflowGraphContract.ValidateAndCountNodes(mapped.Document.ToJsonString(), maxNodes: 200);
    }

    /// <summary>
    ///     The two shapes the mapper can make nothing of: valid JSON that is not a canvas graph, and a member of the
    ///     wrong type. Both answer an empty document with a reason, which is what carries the row through to an
    ///     operator instead of losing it behind a log line.
    /// </summary>
    [Test]
    [Arguments(CanvasGraphs.NotAGraph)]
    [Arguments(CanvasGraphs.NodesNotAnArray)]
    public void MapGraph_OverJsonThatIsNotACanvasGraph_AnswersAnEmptyDocumentAndAReason(string canvasGraph)
    {
        var mapped = CanvasWorkflowImport.MapGraph(canvasGraph);

        AssertEx.Empty(Nodes(mapped.Document));
        AssertEx.Contains(string.Join(" ", mapped.Reasons), "nothing could be translated from it");
    }

    private static IEnumerable<JsonNode?> Nodes(JsonNode document) =>
        (document["nodes"] as JsonArray) ?? [];

    private static IEnumerable<JsonNode?> Edges(JsonNode document) =>
        (document["edges"] as JsonArray) ?? [];

    private static JsonNode Node(JsonNode document, string key) =>
        AssertEx.NotNull(Nodes(document).FirstOrDefault(node => node!["key"]!.GetValue<string>() == key), $"the document declares no node '{key}'.");

    private static string Keys(JsonNode document, string member) =>
        string.Join(", ", (document[member] as JsonArray ?? []).Select(static element => element!["key"]!.GetValue<string>()));

    private static string Wiring(JsonNode document) =>
        string.Join(", ", Edges(document).Select(static edge => $"{edge!["from"]!.GetValue<string>()}->{edge["to"]!.GetValue<string>()}"));
}
