namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One thing wrong with a definition graph, keyed by the node or edge it belongs to so the editor can draw it on
///     the offending element rather than in a list beside the canvas.
///     <para>
///         <see cref="Key" /> is <see langword="null" /> for a failure that belongs to the document as a whole — a
///         malformed body, a schema version this node does not speak, or one of the structural rules whose answer is
///         about the graph rather than about any one part of it.
///     </para>
/// </summary>
public sealed record GraphWorkflowValidationError(string? Key, string Message)
{
    public override string ToString() =>
        Key is null ? Message : $"{Key}: {Message}";
}

/// <summary>
///     Everything wrong with one graph. <see cref="IsValid" /> is true only when <see cref="Errors" /> is empty.
///     <para>
///         Accumulated rather than thrown one at a time because an author fixing a canvas wants every complaint at
///         once. The whole-document and structural rules are the deliberate exception and still throw first: there is
///         nothing useful to say about the rest of a graph nobody can walk.
///     </para>
/// </summary>
public sealed record GraphWorkflowValidationResult(IReadOnlyList<GraphWorkflowValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static GraphWorkflowValidationResult Valid { get; } = new([]);

    public static GraphWorkflowValidationResult Invalid(IReadOnlyList<GraphWorkflowValidationError> errors) =>
        new(errors);
}

/// <summary>
///     Bad input to the graph workflow runtime: a graph that cannot be parsed, or one whose nodes and edges the
///     dispatcher could not route. Carries the structured errors, which is what lets the endpoints replay them one by
///     one instead of collapsing them into a single sentence.
/// </summary>
public sealed class GraphWorkflowValidationException : InvalidOperationException
{
    /// <summary>A single whole-document or structural failure — the throw-first half of the rule set.</summary>
    public GraphWorkflowValidationException(string message)
        : base(message) =>
        Result = new GraphWorkflowValidationResult([new GraphWorkflowValidationError(Key: null, message)]);

    /// <summary>Every per-node and per-edge failure a structurally sound graph collected.</summary>
    public GraphWorkflowValidationException(GraphWorkflowValidationResult result)
        : base(BuildMessage(result)) =>
        Result = result;

    public GraphWorkflowValidationResult Result { get; }

    private static string BuildMessage(GraphWorkflowValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Count == 0
            ? "The graph workflow definition is invalid."
            : $"The graph workflow definition is invalid: {string.Join("; ", result.Errors)}";
    }
}
