namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text;
using System.Text.Json;

/// <summary>One upstream node's full output document, keyed by the node it came from.</summary>
internal sealed record GraphWorkflowUpstreamDocument(string NodeKey, string OutputDocumentJson);

/// <summary>
///     An output document that would not fit. Its own type rather than a return code because every caller does the same
///     thing with it — fail the node run <c>OutputTooLarge</c> — and a result nobody branches on is a branch at every
///     call site for nothing.
/// </summary>
internal sealed class GraphWorkflowOutputTooLargeException(string nodeKey, int bytes, int maxBytes)
    : InvalidOperationException($"Node '{nodeKey}' produced a {bytes}-byte output document, more than the {maxBytes} bytes one node run may store.")
{
    public string NodeKey { get; } = nodeKey;
}

/// <summary>
///     The single writer of every node-run document. No executor composes one itself: eight node kinds share one
///     envelope, one <c>branch</c> derivation and one size cap, and a second implementation of any of those is a way
///     for an executor to disagree with the routing the dispatcher will do.
///     <para>
///         Pure and static — no I/O, no options container. The one option this needs, the output cap, travels as an
///         argument for the same reason the parser's node cap does: it keeps the whole class testable without a host.
///     </para>
/// </summary>
internal static class GraphWorkflowDocuments
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The document a node produced nothing for. Cloned once: an element outlives the reader that made it.</summary>
    public static JsonElement EmptyObject { get; } = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal), JsonOptions);

    /// <summary>A JSON <c>null</c>, which is what "there was no value here" serializes as.</summary>
    private static JsonElement NullValue { get; } = JsonSerializer.SerializeToElement<object?>(value: null, JsonOptions);

    /// <summary>
    ///     The common envelope: <c>{ status, attempt, branch, output }</c>, with <c>branch</c> naming the out-edge that
    ///     fired.
    ///     <para>
    ///         The branch is derived HERE, from the envelope this call has just built, by evaluating the node's own
    ///         out-edge conditions against it — first match wins, and an unconditional edge names none, because an edge
    ///         that accepts everything says nothing about which way the run went. That is one implementation for all
    ///         eight kinds, and it is the same evaluation the dispatcher will do a moment later against the same bytes.
    ///     </para>
    /// </summary>
    /// <exception cref="GraphWorkflowOutputTooLargeException">The composed document is over <paramref name="maxOutputJsonBytes" /> UTF-8 bytes.</exception>
    public static string Compose(GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        int attempt,
        string status,
        JsonElement output,
        int maxOutputJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        var unrouted = Serialize(new OutputDocument(status, attempt, Branch: null, output));
        var branch = BranchOf(graph, node, unrouted);
        var document = branch is null ? unrouted : Serialize(new OutputDocument(status, attempt, branch, output));

        // UTF-8 bytes, because that is what the column stores — a character count would let a document of astral-plane
        // text through at four times the cap it was measured against.
        var bytes = Encoding.UTF8.GetByteCount(document);
        return bytes <= maxOutputJsonBytes ? document : throw new GraphWorkflowOutputTooLargeException(node.NodeKey, bytes, maxOutputJsonBytes);
    }

    /// <summary>
    ///     The input document an executor is handed: <c>{ run: { input }, upstream: { … }, input: … }</c>.
    ///     <para>
    ///         <c>input</c> is the shortcut for the common shape — the single satisfied predecessor's whole output
    ///         document — and falls back to the <c>upstream</c> map when there is more than one, so a node with two
    ///         inbound edges still has one place to read them all from. With no predecessor at all it is
    ///         <see langword="null" />, which is what the <c>Start</c> node sees.
    ///     </para>
    /// </summary>
    public static string ComposeInput(string? runInputJson, IReadOnlyList<GraphWorkflowUpstreamDocument> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var byKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var document in upstream.OrderBy(static entry => entry.NodeKey, StringComparer.Ordinal))
        {
            byKey[document.NodeKey] = ValueOf(document.OutputDocumentJson);
        }

        var upstreamElement = JsonSerializer.SerializeToElement(byKey, JsonOptions);
        var input = upstream.Count switch
        {
            0 => NullValue,
            1 => ValueOf(upstream[0].OutputDocumentJson),
            _ => upstreamElement
        };
        return Serialize(new InputDocument(new RunInput(ValueOf(runInputJson)), upstreamElement, input));
    }

    /// <summary><c>Start</c>: the run's own input, handed to everything downstream.</summary>
    public static JsonElement StartOutput(string? runInputJson) =>
        JsonSerializer.SerializeToElement(new StartOutputPayload(ValueOf(runInputJson)), JsonOptions);

    /// <summary>
    ///     <c>Condition</c> and <c>Parallel</c>: a verbatim pass-through of the predecessor's <c>output</c>.
    ///     <para>
    ///         This is what makes a <c>Condition</c> node a real router. Edge conditions evaluate against the SOURCE
    ///         node's output document — which for a Condition's own out-edges is the Condition's — so without the
    ///         pass-through they would inspect <c>{}</c> and never fire.
    ///     </para>
    ///     <para>
    ///         Read off <c>input.output</c> of the node's input document, which is the single satisfied predecessor's
    ///         document. A node with several predecessors has no single upstream output to carry forward, and answers
    ///         <c>{}</c> rather than inventing one.
    ///     </para>
    /// </summary>
    public static JsonElement PassThroughOutput(string? inputDocumentJson)
    {
        if (Read(inputDocumentJson) is not { ValueKind: JsonValueKind.Object } input
            || !input.TryGetProperty("input", out var predecessor)
            || predecessor.ValueKind != JsonValueKind.Object
            || !predecessor.TryGetProperty("output", out var output))
        {
            return EmptyObject;
        }

        return output.Clone();
    }

    /// <summary>
    ///     <c>Tool</c>: the invocation's answer under <c>result</c>.
    ///     <para>
    ///         Embedded as JSON when the tool answered with an object or an array, and as a string otherwise. That one
    ///         try-parse is what lets a downstream <c>Condition</c> — which passes its predecessor's output through
    ///         verbatim — dot-path into a structured answer. Ceiling, stated rather than hidden: plain text that
    ///         happens to be a JSON object is embedded as JSON, and the tools that do that mean it.
    ///     </para>
    /// </summary>
    public static JsonElement ToolOutput(string? result) =>
        JsonSerializer.SerializeToElement(new ToolOutputPayload(Read(result) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } structured
            ? structured
            : JsonSerializer.SerializeToElement(result ?? string.Empty, JsonOptions)),
            JsonOptions);

    /// <summary>
    ///     A dot path resolved against a stored document, or <see langword="null" /> when the document does not carry
    ///     it. Shared with the <c>Tool</c> lane's argument bindings, so a binding's path grammar is the one this
    ///     module already has rather than a second walk that could come to disagree with it.
    /// </summary>
    public static JsonElement? Resolve(string? documentJson, string path) =>
        Resolve(Read(documentJson), path);

    /// <summary>
    ///     <c>Join</c>: the per-source map over its satisfied inbound edges, so everything downstream of a join sees
    ///     every branch rather than whichever one the shortcut would have picked.
    /// </summary>
    public static JsonElement JoinOutput(IReadOnlyList<GraphWorkflowUpstreamDocument> satisfied)
    {
        ArgumentNullException.ThrowIfNull(satisfied);

        var byKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var document in satisfied.OrderBy(static entry => entry.NodeKey, StringComparer.Ordinal))
        {
            byKey[document.NodeKey] = ValueOf(document.OutputDocumentJson);
        }

        return JsonSerializer.SerializeToElement(byKey, JsonOptions);
    }

    /// <summary>
    ///     <c>End</c>: the declared outcome and the run's result — <paramref name="resultPath" /> resolved against the
    ///     End node's input document, or that whole document when the author named no path.
    ///     <para>
    ///         A path that is not a dot path, or that the document does not carry, resolves to <c>null</c>. Failing the
    ///         node instead would end a run that did all of its work over a projection nobody reads.
    ///     </para>
    /// </summary>
    public static JsonElement EndOutput(string outcome, string? resultPath, string? inputDocumentJson)
    {
        var input = Read(inputDocumentJson);
        var result = resultPath is null
            ? input ?? NullValue
            : Resolve(input, resultPath) ?? NullValue;
        return JsonSerializer.SerializeToElement(new EndOutputPayload(outcome, result), JsonOptions);
    }

    /// <summary>
    ///     Which of <paramref name="node" />'s out-edges fired, by label. Conditional edges only: an unconditional edge
    ///     accepts every document, so naming it would report a branch for a node that did not choose one.
    /// </summary>
    private static string? BranchOf(GraphWorkflowGraph graph, GraphWorkflowGraphNode node, string documentJson)
    {
        using var document = JsonDocument.Parse(documentJson);
        var envelope = document.RootElement;
        return graph.OutboundEdges(node.NodeKey)
                    .Where(edge => edge.Condition is not null && GraphWorkflowCondition.Evaluate(edge.Condition, envelope))
                    .Select(static edge => edge.Label)
                    .FirstOrDefault();
    }

    /// <summary>Walks a dot path one property name at a time. Anything that is not a dot path resolves to nothing.</summary>
    private static JsonElement? Resolve(JsonElement? document, string path)
    {
        if (document is not { } current || !GraphWorkflowTokens.IsDotPath(path))
        {
            return null;
        }

        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>A stored document as an element, or <see langword="null" /> when it is absent or unreadable.</summary>
    private static JsonElement? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The same read, with unreadable and absent both answering a JSON <c>null</c> the envelope can carry.</summary>
    private static JsonElement ValueOf(string? json) =>
        Read(json) ?? NullValue;

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    ///     The envelope every kind produces. <c>branch</c> is written even when it is null: a reader asking which way
    ///     the run went must be able to tell "no branch fired" from "this document predates branches".
    /// </summary>
    private sealed record OutputDocument(string Status, int Attempt, string? Branch, JsonElement Output);

    private sealed record InputDocument(RunInput Run, JsonElement Upstream, JsonElement Input);

    private sealed record RunInput(JsonElement Input);

    private sealed record StartOutputPayload(JsonElement Input);

    private sealed record ToolOutputPayload(JsonElement Result);

    private sealed record EndOutputPayload(string Outcome, JsonElement Result);
}
