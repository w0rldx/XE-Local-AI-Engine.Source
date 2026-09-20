namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One thing wrong with a definition graph, keyed by the node or edge it belongs to so the editor can draw it on
///     the offending element rather than in a list beside the canvas.
/// </summary>
/// <remarks>
///     <see cref="Key" /> is <see langword="null" /> for a failure that belongs to the document as a whole — a
///     malformed body, a schema version this node does not speak, or one of the structural rules whose answer is
///     about the graph rather than about any one part of it.
/// </remarks>
public sealed record GraphWorkflowValidationError(string? Key, string Message)
{
    public override string ToString() =>
        Key is null ? Message : $"{Key}: {Message}";
}

/// <summary>
///     Everything wrong with one graph. <see cref="IsValid" /> is true only when <see cref="Errors" /> is empty.
/// </summary>
/// <remarks>
///     Accumulated rather than thrown one at a time because an author fixing a canvas wants every complaint at once.
///     The whole-document and structural rules are the deliberate exception and still throw first: there is nothing
///     useful to say about the rest of a graph nobody can walk.
/// </remarks>
public sealed class GraphWorkflowValidationResult
{
    public required IReadOnlyList<GraphWorkflowValidationError> Errors { get; init; }

    /// <summary>Things worth saying about a graph that is nonetheless fine.</summary>
    /// <remarks>
    ///     A warning NEVER blocks: a definition carrying one saves, validates as <see cref="IsValid" />, and runs.
    ///     That is why this is a second list rather than a severity member on
    ///     <see cref="GraphWorkflowValidationError" />, where every consumer of <see cref="Errors" /> would then have
    ///     to remember to filter it out before refusing.
    /// </remarks>
    public IReadOnlyList<GraphWorkflowValidationError> Warnings { get; init; } = [];

    public bool IsValid => Errors.Count == 0;

    public static GraphWorkflowValidationResult Invalid(IReadOnlyList<GraphWorkflowValidationError> errors) =>
        new() { Errors = errors };

    /// <summary>A clean report that still has something to say. Errors stay empty, so this is <see cref="IsValid" />.</summary>
    public static GraphWorkflowValidationResult ValidWith(IReadOnlyList<GraphWorkflowValidationError> warnings) =>
        new() { Errors = [], Warnings = warnings };
}

/// <summary>
///     Bad input to the graph workflow runtime: a graph that cannot be parsed, or one whose nodes and edges the
///     dispatcher could not route.
/// </summary>
/// <remarks>
///     It carries the structured errors, which is what lets the endpoints replay them one by one instead of
///     collapsing them into a single sentence.
/// </remarks>
public sealed class GraphWorkflowValidationException : InvalidOperationException
{
    /// <summary>A single whole-document or structural failure — the throw-first half of the rule set.</summary>
    public GraphWorkflowValidationException(string message)
        : base(message) =>
        Result = new GraphWorkflowValidationResult { Errors = [new GraphWorkflowValidationError(Key: null, message)] };

    /// <summary>The same single failure, wrapping the parse or validation error that produced it.</summary>
    public GraphWorkflowValidationException(string message, Exception innerException)
        : base(message, innerException) =>
        Result = new GraphWorkflowValidationResult { Errors = [new GraphWorkflowValidationError(Key: null, message)] };

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
