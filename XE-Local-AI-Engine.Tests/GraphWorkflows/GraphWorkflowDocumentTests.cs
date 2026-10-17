namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The single writer of every node-run document, pinned byte for byte. These are the bytes edge conditions route
///     on, so a shape that drifts here is a run that takes the wrong branch rather than a test that reads oddly.
/// </summary>
[Category(TestCategories.Unit)]
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
    ///     Asserted directly: a Condition's own out-edges are evaluated against ITS output document, so
    ///     without the pass-through they would inspect an empty object and no branch would ever fire.
    /// </summary>
    [Test]
    public void Compose_ForAConditionNode_PassesTheUpstreamOutputThroughSoItsOwnEdgeFires()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "analyze",
                OutputDocumentJson = AnalyzeDocument
            }
        ]);

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
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "analyze",
                OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"json":{"requiresReview":false}}}"""
            }
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
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "start",
                OutputDocumentJson = AnalyzeDocument
            }
        ]);

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
            GraphWorkflowDocuments.JoinOutput([
                new GraphWorkflowUpstreamDocument
                {
                    NodeKey = "right",
                    OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}"""
                },
                new GraphWorkflowUpstreamDocument
                {
                    NodeKey = "left",
                    OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}"""
                }
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
        var input = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""", [
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "analyze",
                OutputDocumentJson = AnalyzeDocument
            }
        ]);

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
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "analyze",
                OutputDocumentJson = AnalyzeDocument
            }
        ]);

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
        var input = GraphWorkflowDocuments.ComposeInput(runInputJson: null, [
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "analyze",
                OutputDocumentJson = AnalyzeDocument
            }
        ]);

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
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "left",
                OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}"""
            },
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "right",
                OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}"""
            }
        ]);

        using var parsed = JsonDocument.Parse(input);
        AssertEx.Equal("L", parsed.RootElement.GetProperty("input").GetProperty("left").GetProperty("output").GetProperty("text").GetString());
        AssertEx.Equal("R", parsed.RootElement.GetProperty("upstream").GetProperty("right").GetProperty("output").GetProperty("text").GetString());
    }

    [Test]
    public void ComposeInput_ForANodeWithNoPredecessor_LeavesTheShortcutNull()
    {
        var input = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""", []);

        AssertEx.Equal("""{"run":{"input":{"topic":"latency"},"previous":null},"upstream":{},"input":null}""",
            input,
            "which is what the Start node sees: a run input, no previous run, no upstream, and nothing to shortcut to.");
    }

    /// <summary>
    ///     <c>run.previous</c> is written on every run: an explicit null on the first one, so a binding to it resolves there
    ///     too, and the pinned object afterwards, parsed rather than carried as an escaped string.
    /// </summary>
    [Test]
    public void ComposeInput_CarriesThePinnedPreviousRun()
    {
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var previous = GraphWorkflowDocuments.PreviousRun(runId, completedAtUtcMilliseconds: 1_760_263_200_123, """{"report":"| a | b |"}""");

        AssertEx.Equal("""{"runId":"11111111-2222-3333-4444-555555555555","completedAtUtc":"2025-10-12T10:00:00.123Z","output":{"report":"| a | b |"}}""", previous);

        var input = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""", [], previous);

        AssertEx.Equal(
            """{"run":{"input":{"topic":"latency"},"previous":{"runId":"11111111-2222-3333-4444-555555555555","completedAtUtc":"2025-10-12T10:00:00.123Z","output":{"report":"| a | b |"}}},"upstream":{},"input":null}""",
            input);
        AssertEx.Equal("| a | b |", GraphWorkflowDocuments.Resolve(input, "run.previous.output.report")?.GetString());
        AssertEx.Equal<JsonValueKind?>(JsonValueKind.Null,
            GraphWorkflowDocuments.Resolve(GraphWorkflowDocuments.ComposeInput(runInputJson: null, []), "run.previous")?.ValueKind,
            "a first run resolves run.previous to an explicit null, not to a missing path.");
    }

    /// <summary>
    ///     The default End result is the input document minus <c>run.previous</c>, so a run never carries the one before it;
    ///     an explicit path still reaches the previous run.
    /// </summary>
    [Test]
    public void EndOutput_WithNoResultPath_OmitsRunPrevious_AndAnExplicitPathStillReachesIt()
    {
        var previous = GraphWorkflowDocuments.PreviousRun(Guid.NewGuid(), completedAtUtcMilliseconds: 0, "\"last report\"");
        var document = GraphWorkflowDocuments.ComposeInput("""{"topic":"latency"}""",
            [
                new GraphWorkflowUpstreamDocument
                {
                    NodeKey = "analyze",
                    OutputDocumentJson = AnalyzeDocument
                }
            ],
            previous);
        using var composed = JsonDocument.Parse(document);

        var result = GraphWorkflowDocuments.EndOutput("completed", resultPath: null, document).GetProperty("result");

        AssertEx.False(result.GetProperty("run").TryGetProperty("previous", out _), "the default result drops run.previous.");
        AssertEx.Equal(composed.RootElement.GetProperty("run").GetProperty("input").GetRawText(), result.GetProperty("run").GetProperty("input").GetRawText());
        AssertEx.Equal(composed.RootElement.GetProperty("upstream").GetRawText(), result.GetProperty("upstream").GetRawText());
        AssertEx.Equal(composed.RootElement.GetProperty("input").GetRawText(), result.GetProperty("input").GetRawText());
        AssertEx.Equal("last report",
            GraphWorkflowDocuments.EndOutput("completed", "run.previous.output", document).GetProperty("result").GetString(),
            "an explicit path still resolves the previous run.");
    }

    /// <summary>A completed run that resolved no result still pins its id and instant; its output is null.</summary>
    [Test]
    public void PreviousRun_OfARunWithoutAResult_WritesANullOutput()
    {
        var previous = GraphWorkflowDocuments.PreviousRun(Guid.Empty, completedAtUtcMilliseconds: 0, outputJson: null);

        AssertEx.Equal("""{"runId":"00000000-0000-0000-0000-000000000000","completedAtUtc":"1970-01-01T00:00:00Z","output":null}""", previous);
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
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "left",
                OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"L"}}"""
            },
            new GraphWorkflowUpstreamDocument
            {
                NodeKey = "right",
                OutputDocumentJson = """{"status":"succeeded","attempt":1,"branch":null,"output":{"text":"R"}}"""
            }
        ]);

        AssertEx.Equal("{}", GraphWorkflowDocuments.PassThroughOutput(input).GetRawText());
    }
}
