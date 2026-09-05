namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The five node kinds whose work is a pure function of rows the tick has already read: <c>Start</c>, <c>End</c>,
///     <c>Condition</c>, <c>Parallel</c> and <c>Join</c>.
///     <para>
///         They run INSIDE the tick, with no <c>Queued</c> hop, because they wait for no slot — a queued row would be
///         the row lying about what it is waiting for. Two writes and therefore two event rows, which is what makes the
///         timing of a fan-out visible at all and the whole reason <c>Parallel</c> and <c>Join</c> exist as kinds.
///     </para>
///     <para>
///         It composes no document itself: <see cref="GraphWorkflowDocuments" /> is the single writer of every one of
///         them, so an executor cannot disagree with the routing the dispatcher will do a moment later.
///     </para>
/// </summary>
internal sealed class GraphWorkflowInlineExecutor(IOptions<GraphWorkflowOptions> options)
{
    private readonly GraphWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>The kinds this executor owns. Everything else is a lane's, or has no executor in this build.</summary>
    public static bool Owns(GraphWorkflowNodeKind kind) =>
        kind is GraphWorkflowNodeKind.Start
            or GraphWorkflowNodeKind.End
            or GraphWorkflowNodeKind.Condition
            or GraphWorkflowNodeKind.Parallel
            or GraphWorkflowNodeKind.Join;

    /// <summary>
    ///     Runs one eligible node run to its terminal and answers how many transitions it wrote.
    ///     <para>
    ///         The input document is composed over the SATISFIED inbound edges, which is the same set the admission
    ///         that got here judged — so what the node reads is exactly what let it run.
    ///     </para>
    /// </summary>
    public async Task<int> ExecuteAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> byKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);
        ArgumentNullException.ThrowIfNull(byKey);

        var upstream = Upstream(graph, node, byKey);
        var inputJson = GraphWorkflowDocuments.ComposeInput(run.InputJson, upstream);

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Running,
                               InputJson: inputJson),
                           cancellationToken)
                       .ConfigureAwait(false);

        string document;
        try
        {
            document = GraphWorkflowDocuments.Compose(graph,
                node,
                nodeRun.Attempt,
                GraphWorkflowNodeOutputStatuses.Succeeded,
                Output(node, run, inputJson, upstream),
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException exception)
        {
            // Not retryable, and deliberately: the same rows compose the same bytes next time. A pass-through chain is
            // where this earns its keep — every hop re-measures the document it is carrying forward.
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Failed,
                                   FailureClass: GraphWorkflowFailureClass.OutputTooLarge,
                                   TerminalReason: exception.Message),
                               cancellationToken)
                           .ConfigureAwait(false);
            return 2;
        }

        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Succeeded,
                               OutputJson: document),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    ///     The run's result, read off a succeeded <c>End</c> node's output document. The document's own
    ///     <c>output.result</c> is the whole of it — the outcome beside it names what KIND of end this was, and that is
    ///     the node run's answer rather than the run's.
    /// </summary>
    public static string? RunResult(string? endOutputJson)
    {
        if (string.IsNullOrWhiteSpace(endOutputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(endOutputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("output", out var output)
                   && output.ValueKind == JsonValueKind.Object
                   && output.TryGetProperty("result", out var result)
                ? result.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The kind-specific <c>output</c>, per the binding document contract.</summary>
    private static JsonElement Output(GraphWorkflowGraphNode node,
        GraphWorkflowRunSnapshot run,
        string inputJson,
        IReadOnlyList<GraphWorkflowUpstreamDocument> upstream) =>
        node.Kind switch
        {
            GraphWorkflowNodeKind.Start => GraphWorkflowDocuments.StartOutput(run.InputJson),

            // Pass-through, which is what makes a Condition a real ROUTER: its own out-edges are evaluated against its
            // own output document, so without the predecessor's payload they would inspect {} and never fire.
            GraphWorkflowNodeKind.Condition or GraphWorkflowNodeKind.Parallel => GraphWorkflowDocuments.PassThroughOutput(inputJson),
            GraphWorkflowNodeKind.Join => GraphWorkflowDocuments.JoinOutput(upstream),
            GraphWorkflowNodeKind.End => GraphWorkflowDocuments.EndOutput(((GraphWorkflowEndConfig)node.Config).Outcome,
                ((GraphWorkflowEndConfig)node.Config).ResultPath,
                inputJson),
            _ => GraphWorkflowDocuments.EmptyObject
        };

    /// <summary>
    ///     The output documents of the predecessors this node run may read: the sources of its SATISFIED inbound edges,
    ///     each once even when two edges connect the same pair.
    ///     <para>
    ///         Shared with the lanes rather than copied into them: an executor that composed this set differently would
    ///         hand its node a different world than the admission that let it run judged.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<GraphWorkflowUpstreamDocument> Upstream(GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> byKey) =>
    [
        .. graph.InboundEdges(node.NodeKey)
                .Where(edge => GraphWorkflowStateMachine.EdgeState(edge, byKey) == GraphWorkflowEdgeState.Satisfied)
                .Select(static edge => edge.From)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                // A satisfied edge means a succeeded source, so the document is there; the fallback is what an
                // executor that wrote no output would leave, and the composer reads an empty string as a JSON null.
                .Select(key => new GraphWorkflowUpstreamDocument(key, byKey[key].OutputJson ?? string.Empty))
    ];
}
