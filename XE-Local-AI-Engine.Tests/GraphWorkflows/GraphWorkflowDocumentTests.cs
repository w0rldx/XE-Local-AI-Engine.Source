namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The single writer of every node-run document, pinned byte for byte. These are the bytes edge conditions route
///     on, so a shape that drifts here is a run that takes the wrong branch rather than a test that reads oddly.
/// </summary>
public sealed class GraphWorkflowDocumentTests
{
    /// <summary>Comfortably above anything these documents produce, so only the cap test is about the cap.</summary>
    private const int RoomToSpare = 64 * 1024;

    /// <summary>What an Agent node produced: the shape a Condition downstream of it has to be able to read.</summary>
    private const string AnalyzeDocument = """
                                           {"status":"succeeded","attempt":1,"branch":null,"output":{"text":"ok","json":{"requiresReview":true}}}
                                           """;

    [Test]
    public void Compose_ForAStartNode_WritesTheEnvelopeAroundTheRunsOwnInput()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["start"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.StartOutput("""{"topic":"latency"}"""),
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"input":{"topic":"latency"}}}""",
            document,
            "the envelope is camelCase with branch written even when null, and the run input travels verbatim.");
    }

    [Test]
    public void StartOutput_WithNoRunInput_CarriesAJsonNullRatherThanNothing()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["start"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.StartOutput(runInputJson: null),
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"input":null}}""",
            document,
            "a run started with no input still has an input member, so a downstream path reads null rather than missing.");
    }

    /// <summary>
    ///     Ruling C1, asserted directly: a Condition's own out-edges are evaluated against ITS output document, so
    ///     without the pass-through they would inspect an empty object and no branch would ever fire.
    /// </summary>
    [Test]
    public void Compose_ForAConditionNode_PassesTheUpstreamOutputThroughSoItsOwnEdgeFires()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [new GraphWorkflowUpstreamDocument("analyze", AnalyzeDocument)]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["check"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.PassThroughOutput(input),
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":"yes","output":{"text":"ok","json":{"requiresReview":true}}}""",
            document,
            "the Condition carries the Agent's output verbatim, and its own 'yes' edge on output.json.requiresReview fires on it.");
    }

    [Test]
    public void Compose_ForAConditionNode_RecordsTheOtherBranchWhenTheFirstOneDoesNotFire()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null,
        [
            new GraphWorkflowUpstreamDocument("analyze", """{"status":"succeeded","attempt":1,"branch":null,"output":{"json":{"requiresReview":false}}}""")
        ]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["check"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.PassThroughOutput(input),
            RoomToSpare);

        AssertEx.Contains(document, "\"branch\":\"no\"", message: "the branch names the edge that fired, not the first edge declared.");
    }

    /// <summary>
    ///     An unconditional out-edge names no branch. It accepts every document, so reporting it would say the node
    ///     chose a way to go when in fact it had only one.
    /// </summary>
    [Test]
    public void Compose_WhenNoOutEdgeIsConditional_LeavesTheBranchNull()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["analyze"],
            attempt: 2,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.EmptyObject,
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":2,"branch":null,"output":{}}""", document);
    }

    [Test]
    public void Compose_ForAParallelNode_PassesTheUpstreamOutputThroughUnchanged()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAll);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [new GraphWorkflowUpstreamDocument("start", AnalyzeDocument)]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["fanout"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.PassThroughOutput(input),
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"ok","json":{"requiresReview":true}}}""",
            document,
            "Parallel is pass-through for the same reason Condition is: a fan-out must not blank the data its branches route on.");
    }

    [Test]
    public void JoinOutput_EmitsThePerSourceMapOverEverySatisfiedEdge()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAll);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["merge"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.JoinOutput(
            [
                new GraphWorkflowUpstreamDocument("right", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}"""),
                new GraphWorkflowUpstreamDocument("left", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}""")
            ]),
            RoomToSpare);

        AssertEx.Equal("""
                       {"status":"succeeded","attempt":1,"branch":null,"output":{"left":{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}},"right":{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}}}
                       """.Trim(),
            document,
            "everything after a join sees every branch's WHOLE document, keyed by source and ordered by key so the bytes are stable.");
    }

    [Test]
    public void EndOutput_WithNoResultPath_CarriesTheWholeInputDocument()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);
        var input = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""", [new GraphWorkflowUpstreamDocument("analyze", AnalyzeDocument)]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["done"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.EndOutput("completed", resultPath: null, input),
            RoomToSpare);

        using var parsed = JsonDocument.Parse(document);
        var output = parsed.RootElement.GetProperty("output");
        AssertEx.Equal("completed", output.GetProperty("outcome").GetString());
        AssertEx.Equal("latency", output.GetProperty("result").GetProperty("run").GetProperty("input").GetProperty("topic").GetString());
        AssertEx.True(output.GetProperty("result").TryGetProperty("upstream", out _), "the whole input document is the fallback, upstream map included.");
    }

    [Test]
    public void EndOutput_WithAResultPath_ProjectsThatPathOutOfTheInputDocument()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.EndWithResultPath);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [new GraphWorkflowUpstreamDocument("analyze", AnalyzeDocument)]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["done"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.EndOutput("completed", "input.output.json", input),
            RoomToSpare);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"outcome":"completed","result":{"requiresReview":true}}}""",
            document,
            "the declared path is resolved against the End node's input document and nothing else travels.");
    }

    /// <summary>
    ///     A path the document does not carry resolves to null rather than failing the node: a run that did all of its
    ///     work must not end on a projection nobody reads.
    /// </summary>
    [Test]
    public void EndOutput_WithAPathTheDocumentDoesNotCarry_ResolvesToNull()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.EndWithResultPath);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [new GraphWorkflowUpstreamDocument("analyze", AnalyzeDocument)]);

        var document = GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["done"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            GraphWorkflowDocuments.EndOutput("completed", "input.output.missing", input),
            RoomToSpare);

        AssertEx.Contains(document, "\"result\":null", message: "an unresolvable projection is null, not a failure.");
    }

    [Test]
    public void ComposeInput_WithSeveralPredecessors_ShortcutsToTheWholeUpstreamMap()
    {
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null,
        [
            new GraphWorkflowUpstreamDocument("left", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}"""),
            new GraphWorkflowUpstreamDocument("right", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}""")
        ]);

        using var parsed = JsonDocument.Parse(input);
        AssertEx.Equal("L", parsed.RootElement.GetProperty("input").GetProperty("left").GetProperty("output").GetProperty("text").GetString());
        AssertEx.Equal("R", parsed.RootElement.GetProperty("upstream").GetProperty("right").GetProperty("output").GetProperty("text").GetString());
    }

    [Test]
    public void ComposeInput_ForANodeWithNoPredecessor_LeavesTheShortcutNull()
    {
        var input = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""", []);

        AssertEx.Equal("""{"run":{"input":{"topic":"latency"}},"upstream":{},"input":null}""",
            input,
            "which is what the Start node sees: a run input, no upstream, and nothing to shortcut to.");
    }

    /// <summary>
    ///     The cap earns its keep on the pass-through hop: a chain of Condition nodes carries the same document
    ///     forward, so it is measured again on every one of them.
    /// </summary>
    [Test]
    public void Compose_WhenTheDocumentIsOverTheCap_FailsTheNodeRatherThanStoringIt()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var oversized = GraphWorkflowDocuments.StartOutput($$"""{"blob":"{{new string('x', 512)}}"}""");

        var thrown = AssertEx.Throws<GraphWorkflowOutputTooLargeException>(() => GraphWorkflowDocuments.Compose(graph,
                graph.Nodes["check"],
                attempt: 1,
                GraphWorkflowNodeOutputStatuses.Succeeded,
                oversized,
                maxOutputJsonBytes: 128),
            "the cap is measured on the composed document, which is the thing that would be stored.");

        AssertEx.Equal("check", thrown.NodeKey, "and it names the node, because that is the row the failure is written to.");
    }

    /// <summary>UTF-8 bytes, not characters: a document of astral-plane text must not slip through at a quarter of its real size.</summary>
    [Test]
    public void Compose_MeasuresTheCapInUtf8BytesRatherThanCharacters()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        // 20 emoji: 20 UTF-16 pairs, but 80 bytes of UTF-8 once escaped into the document.
        var text = string.Concat(Enumerable.Repeat("😀", 20));
        var output = GraphWorkflowDocuments.StartOutput($$"""{"blob":"{{text}}"}""");

        _ = AssertEx.Throws<GraphWorkflowOutputTooLargeException>(() => GraphWorkflowDocuments.Compose(graph,
            graph.Nodes["start"],
            attempt: 1,
            GraphWorkflowNodeOutputStatuses.Succeeded,
            output,
            maxOutputJsonBytes: 100));
    }

    /// <summary>
    ///     A node with more than one predecessor has no single upstream output to carry forward. Answering an empty
    ///     object is the honest reading; picking one of them would route the run on a branch nobody chose.
    /// </summary>
    [Test]
    public void PassThroughOutput_WithNoSinglePredecessor_IsAnEmptyObject()
    {
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null,
        [
            new GraphWorkflowUpstreamDocument("left", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}"""),
            new GraphWorkflowUpstreamDocument("right", """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}""")
        ]);

        AssertEx.Equal("{}", GraphWorkflowDocuments.PassThroughOutput(input).GetRawText());
    }
}
