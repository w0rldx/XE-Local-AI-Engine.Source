namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What the definition list returns: no graph blob, so listing never decrypts one.</summary>
public sealed record GraphWorkflowDefinitionSummary(
    Guid Id,
    string Name,
    string? Description,
    string GraphHash,
    int NodeCount,
    int SchemaVersion,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     One definition in full. <see cref="GraphJson" /> is the authored document as text: this assembly may reference
///     only <c>Providers.Abstractions</c>, so the parsed graph is the Application layer's type and never appears here.
/// </summary>
public sealed record GraphWorkflowDefinitionSnapshot(
    Guid Id,
    string Name,
    string? Description,
    string GraphJson,
    string GraphHash,
    int NodeCount,
    int SchemaVersion,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     The canonical node-run field set: one row of <c>graph_workflow_node_runs</c> with its encrypted columns decoded
///     to text. The state machine reads <see cref="NodeKey" />, <see cref="Kind" />, <see cref="Status" /> and
///     <see cref="OutputJson" />; the rest is what a run-detail read returns. It lives here rather than in the
///     Application layer so the run store can return it without an Application reference this assembly may not have.
/// </summary>
public sealed record GraphWorkflowNodeRunSnapshot(
    Guid Id,
    Guid RunId,
    string NodeKey,
    GraphWorkflowNodeKind Kind,
    GraphWorkflowNodeRunStatus Status,
    int Attempt,
    GraphWorkflowDecisionKind? PendingDecisionKind,
    Guid? DecisionOperationId,
    string? DecidedBySubject,
    GraphWorkflowFailureClass FailureClass,
    string? Error,
    string? InputJson,
    string? OutputJson,
    Guid? InvocationId,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long UpdatedAtUtc);

public sealed record CreateGraphWorkflowDefinitionCommand(
    Guid DefinitionId,
    string Name,
    string GraphJson,
    int NodeCount,
    int SchemaVersion = 1,
    string? Description = null);

/// <summary>
///     A partial edit: every optional member left null means "leave it alone", which is what lets a rename travel
///     without the caller re-sending a graph it never read.
/// </summary>
public sealed record UpdateGraphWorkflowDefinitionCommand(
    Guid DefinitionId,
    int ExpectedVersion,
    string? Name = null,
    string? Description = null,
    string? GraphJson = null,
    int? NodeCount = null,
    int? SchemaVersion = null);

/// <summary>
///     The durable substrate for Graph Workflow definitions.
///     <para>
///         The graph hash and the node count are written store-side, together with the graph, at every save — that is
///         what lets <see cref="IGraphWorkflowStore.ListDefinitionsAsync" /> promise never to decrypt a blob and still
///         tell the truth about one.
///     </para>
/// </summary>
public interface IGraphWorkflowStore
{
    Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Optimistic: a stale <c>ExpectedVersion</c> loses with <see cref="GraphWorkflowDefinitionConflictException" />.</summary>
    Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Never loads <c>graph_json</c>: the node count is the denormalized column, not a parse.</summary>
    Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A hard delete, refused with <see cref="GraphWorkflowDefinitionConflictException" /> while any run that pins
    ///     this definition is still live — checked INSIDE the transaction, so a run that starts mid-delete still wins.
    ///     Terminal runs are unaffected: each pinned its own copy of the graph at start, so history survives the row.
    /// </summary>
    Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);
}

public sealed class GraphWorkflowNotFoundException(string message) : InvalidOperationException(message);

/// <summary>
///     Both ways a definition write can lose, under one type because from the client's side they are one story —
///     somebody else got there first: a stale <c>version</c> on an update, and a delete refused while a live run pins
///     the definition. Maps to a 409 through <c>ConflictExceptionHandler</c>.
/// </summary>
public sealed class GraphWorkflowDefinitionConflictException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

/// <summary>
///     The rejection channel for a move the run state machine forbids. Declared here with the rest of the family and
///     unthrown in this slice: the definition half has no transition to refuse. The run store is its first caller.
/// </summary>
public sealed class GraphWorkflowInvalidTransitionException(string message) : InvalidOperationException(message);
