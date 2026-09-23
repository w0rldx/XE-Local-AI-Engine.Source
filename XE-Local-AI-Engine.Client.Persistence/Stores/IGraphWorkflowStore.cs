namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What the definition list returns: no graph blob, so listing never decrypts one.</summary>
public sealed class GraphWorkflowDefinitionSummary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required int SchemaVersion { get; init; }

    public required GraphWorkflowDefinitionKind Kind { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     One definition in full. <see cref="GraphJson" /> is the authored document as text: this assembly may reference
///     only <c>Providers.Abstractions</c>, so the parsed graph is the Application layer's type and never appears here.
/// </summary>
public sealed class GraphWorkflowDefinitionSnapshot
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string GraphJson { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required int SchemaVersion { get; init; }

    public required GraphWorkflowDefinitionKind Kind { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     The canonical node-run field set: one row of <c>graph_workflow_node_runs</c> with its encrypted columns decoded
///     to text.
/// </summary>
/// <remarks>
///     The state machine reads <see cref="NodeKey" />, <see cref="Kind" />, <see cref="Status" /> and
///     <see cref="OutputJson" />; the rest is what a run-detail read returns. It lives here rather than in the
///     Application layer so the run store can return it without an Application reference this assembly may not have.
/// </remarks>
public sealed record GraphWorkflowNodeRunSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required GraphWorkflowNodeKind Kind { get; init; }

    public required GraphWorkflowNodeRunStatus Status { get; init; }

    public required int Attempt { get; init; }

    public required GraphWorkflowDecisionKind? PendingDecisionKind { get; init; }

    public required Guid? DecisionOperationId { get; init; }

    public required string? DecidedBySubject { get; init; }

    public required GraphWorkflowFailureClass FailureClass { get; init; }

    public required string? Error { get; init; }

    public required string? InputJson { get; init; }

    public required string? OutputJson { get; init; }

    public required Guid? InvocationId { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     The sentinel <see cref="Any" /> version.
/// </summary>
/// <remarks>
///     The dispatcher moves node-run status while a human HTTP action may be writing a cancel on the same run, so an
///     ordinary status move — which has no lost update to protect against, because a replayed tick re-derives the
///     same answer from unchanged rows — passes <see cref="Any" /> and never loses that race. A run-level write that
///     must NOT lose it passes the version it read.
/// </remarks>
public static class GraphWorkflowVersions
{
    public const long Any = -1;
}

/// <summary>
///     One run row with its encrypted columns decoded to text. <see cref="GraphJson" /> is the copy pinned at start:
///     the definition row may be edited, or deleted, without changing what this run executes.
/// </summary>
public sealed class GraphWorkflowRunSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RequestId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string GraphHash { get; init; }

    public required GraphWorkflowRunStatus Status { get; init; }

    public required GraphWorkflowFailureClass FailureClass { get; init; }

    public required string GraphJson { get; init; }

    public required string? InputJson { get; init; }

    public required string? OutputJson { get; init; }

    public required long Seq { get; init; }

    public required long Version { get; init; }

    public required long? CancelRequestedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>One entry of a run's append-only change log. <see cref="Seq" /> is its order as well as its watermark.</summary>
public sealed class GraphWorkflowRunEventSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required long Seq { get; init; }

    public required string EventType { get; init; }

    public required string? NodeKey { get; init; }

    public required string? DetailJson { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     What one mutation committed: the run it belongs to and the watermark its event took.
/// </summary>
/// <remarks>
///     Deliberately NOT the post-commit version. A caller that needs one re-reads the run immediately before its next
///     run-level write, because this tick's own node-run writes have already moved it — a version carried out of here
///     would be stale by the time anything used it.
/// </remarks>
public sealed class GraphWorkflowMutationResult
{
    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }
}

/// <summary>
///     One node run to create at start: one per node of the pinned graph, all <c>Pending</c>.
/// </summary>
/// <remarks>
///     Every node run of a graph workflow exists from the moment the run does, which is what lets admission be a pure
///     function of rows. No attempt cap travels here: the node's <c>maxAttempts</c> lives in the pinned graph and the
///     retry stage reads it from there, so a column beside it could only ever disagree with the document the run
///     executes.
/// </remarks>
public sealed class GraphWorkflowNodeRunSeed
{
    public required Guid NodeRunId { get; init; }

    public required string NodeKey { get; init; }

    public required GraphWorkflowNodeKind Kind { get; init; }

    public string? InputJson { get; init; }
}

/// <summary>
///     A run start, as ONE transaction: the run row, one <c>Pending</c> node run per graph node, and the
///     <c>run.created</c> event.
/// </summary>
/// <remarks>
///     The definition's existence and version are re-checked INSIDE that transaction — the obligation
///     <see cref="IGraphWorkflowStore.DeleteDefinitionAsync" /> names. A start that read the definition in one
///     transaction and inserted here in another could pin a definition a delete has already removed.
/// </remarks>
public sealed class StartGraphWorkflowRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid RequestId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string GraphHash { get; init; }

    public required string GraphJson { get; init; }

    public required string? InputJson { get; init; }

    public required IReadOnlyList<GraphWorkflowNodeRunSeed> NodeRuns { get; init; }
}

/// <summary>
///     A run status move.
/// </summary>
/// <remarks>
///     <see cref="SanitizedReason" /> has no column on the run row and is not meant to: it travels into the event's
///     detail, where a reader following the log finds it beside the move it explains. The cancel-requested instant is
///     deliberately NOT a member either: the store stamps it from its own <see cref="TimeProvider" /> on the move to
///     <c>Cancelling</c>, like every other timestamp on these rows, so a caller's clock cannot disagree with the row's.
/// </remarks>
public sealed class TransitionGraphWorkflowRunCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required GraphWorkflowRunStatus TargetStatus { get; init; }

    public GraphWorkflowFailureClass? FailureClass { get; init; }

    public string? SanitizedReason { get; init; }

    public string? OutputJson { get; init; }
}

/// <summary>
///     A node-run status move. <see cref="IncrementAttempt" /> is the retry-in-place path; the row is never
///     duplicated, and the per-attempt history lives in the event log.
/// </summary>
/// <remarks>
///     <see cref="QueueReason" /> and <see cref="TerminalReason" /> both land in the row's ONE reason column — why a
///     row is queued and why it ended are one question at different moments — and a move back to <c>Pending</c> clears
///     it, because a re-attempt must not report the previous attempt's outcome while it runs.
///     <see cref="EventType" /> and <see cref="DetailJson" /> override what the status would derive, for the one move
///     it cannot express: a re-attempt is <c>node.retried</c>, and it has cleared the failure it re-attempts for.
/// </remarks>
public sealed class TransitionGraphWorkflowNodeRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required GraphWorkflowNodeRunStatus TargetStatus { get; init; }

    public string? QueueReason { get; init; }

    public string? OutputJson { get; init; }

    public string? InputJson { get; init; }

    public GraphWorkflowFailureClass? FailureClass { get; init; }

    public string? TerminalReason { get; init; }

    public string? DetailJson { get; init; }

    public Guid? InvocationId { get; init; }

    public GraphWorkflowDecisionKind? PendingDecisionKind { get; init; }

    public bool IncrementAttempt { get; init; }

    public string? EventType { get; init; }
}

/// <summary>
///     A pause's answer, as ONE conditional write. Keyed by the node run rather than by the node key because the
///     Application layer has already read the row it validated the answer against.
/// </summary>
/// <remarks>
///     The store applies it only while the row is still <c>WaitingForApproval</c> with no decision on it — the
///     compare-and-set that makes two concurrent decides settle as one; a write that finds no such row is told so by
///     a <see langword="null" /> result, because losing that race is answered by re-reading, not a failure.
///     <see cref="OutputJson" /> arrives composed: the Application layer owns the one document composer, and a second
///     spelling of a pause's routing document here is how a pre-flight check and a real run disagree on an out-edge.
/// </remarks>
public sealed record DecideGraphWorkflowNodeRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required GraphWorkflowDecisionKind Decision { get; init; }

    public required string? DecidedBySubject { get; init; }

    public required string OutputJson { get; init; }
}

public sealed class AppendGraphWorkflowEventCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required string EventType { get; init; }

    public string? NodeKey { get; init; }

    public string? DetailJson { get; init; }
}

/// <summary>
///     One row a host death stranded, carrying what the runtime needs to judge it without a follow-up read per row.
/// </summary>
/// <remarks>
///     <see cref="Status" /> is what the row held BEFORE the collapse — what it was doing is the useful fact, since
///     where it lands is always <c>Pending</c> unless a repair moves it further.
/// </remarks>
public sealed record GraphWorkflowReconciledNodeRun
{
    public required Guid NodeRunId { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required GraphWorkflowNodeKind Kind { get; init; }

    public required GraphWorkflowNodeRunStatus Status { get; init; }

    public required int Attempt { get; init; }
}

/// <summary>
///     One judged node run: the row as the caller observed it, and what to do with it once the collapse has confirmed
///     it is still that row.
/// </summary>
/// <remarks>
///     A verdict is only true of the state it was decided from, so a row that moved under the caller is left exactly
///     as it is rather than repaired from stale evidence. Every command in <see cref="Repairs" /> MUST carry
///     <see cref="GraphWorkflowVersions.Any" />: the collapse bumps its run's version once per stranded row before any
///     repair is applied, so a repair naming the version its caller read is stale by construction and fails the whole
///     recovery transaction rather than its own row.
/// </remarks>
public sealed class GraphWorkflowNodeRunVerdict
{
    public required Guid NodeRunId { get; init; }

    public required GraphWorkflowNodeRunStatus ObservedStatus { get; init; }

    public required int ObservedAttempt { get; init; }

    public required IReadOnlyList<TransitionGraphWorkflowNodeRunCommand> Repairs { get; init; }
}

/// <summary>
///     Turns a reconciliation into a SETTLING pass: every stranded node run no verdict matched is failed rather than
///     left where it is.
/// </summary>
/// <remarks>
///     Pass it on the last pass only — walking away strands a row nothing downstream picks up again, and there is no
///     <c>Blocked</c> state to park it in.
/// </remarks>
public sealed class GraphWorkflowUnjudgedNodeRunSettlement
{
    public required GraphWorkflowFailureClass FailureClass { get; init; }

    public required string SanitizedReason { get; init; }
}

public sealed class CreateGraphWorkflowDefinitionCommand
{
    public required Guid DefinitionId { get; init; }

    public required string Name { get; init; }

    public required string GraphJson { get; init; }

    public required int NodeCount { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public GraphWorkflowDefinitionKind Kind { get; init; } = GraphWorkflowDefinitionKind.Standard;

    public string? Description { get; init; }
}

/// <summary>
///     A partial edit: every optional member left null means "leave it alone", which is what lets a rename travel
///     without the caller re-sending a graph it never read.
/// </summary>
/// <remarks>
///     With ONE exception: <see cref="NodeCount" /> and <see cref="GraphJson" /> travel TOGETHER or not at all, and
///     either without the other is refused with an <see cref="ArgumentException" />. The count is denormalized so the
///     definition list never decrypts a blob, which makes both halves the same lie: a new graph beside the old count,
///     or a new count beside a graph it was not taken from. <see cref="SchemaVersion" /> stays optional: this node
///     understands one version and the parser refuses every other, so a graph that reached the store IS that version.
/// </remarks>
public sealed class UpdateGraphWorkflowDefinitionCommand
{
    public required Guid DefinitionId { get; init; }

    public required int ExpectedVersion { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? GraphJson { get; init; }

    public int? NodeCount { get; init; }

    public int? SchemaVersion { get; init; }

    /// <summary>Derived from the graph like the node count, so it travels with <see cref="GraphJson" />; null leaves the stored kind alone.</summary>
    public GraphWorkflowDefinitionKind? Kind { get; init; }
}

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
    ///     this definition is still live — checked INSIDE the transaction.
    /// </summary>
    /// <remarks>
    ///     Terminal runs are unaffected: each pinned its own copy of the graph at start, so history survives the row.
    ///     The transaction makes delete-vs-start safe only UNDER A PRECONDITION the run store owes: run start must
    ///     re-read the definition's existence and version inside the SAME transaction that inserts the run row. A
    ///     start that reads first and inserts in a second transaction can pin a run to a definition this delete has
    ///     already removed, because the live-run count here saw nothing — the run did not exist yet.
    /// </remarks>
    Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a run: the run row, one <c>Pending</c> node run per seed and the <c>run.created</c> event, in one
    ///     transaction that re-reads the definition first.
    /// </summary>
    /// <remarks>
    ///     <b>The insert IS the idempotency guarantee.</b> A caller's check-then-act on
    ///     <see cref="FindRunByRequestAsync" /> can be raced by a genuinely concurrent identical start, so this
    ///     inserts first and catches the unique-index violation on <c>request_id</c>: on that catch it rolls back,
    ///     re-reads by request id and answers with the run that WON. The index is the lock; no application-level gate
    ///     is added on top of it.
    /// </remarks>
    Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>The run a caller-minted request id already started, or <see langword="null" />. Never throws for an unknown id.</summary>
    Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Newest first, optionally filtered by status.</summary>
    Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    ///     How many runs are EXECUTING — <c>Running</c>, <c>WaitingForApproval</c> or <c>Cancelling</c> — counted no
    ///     further than <paramref name="probeLimit" /> rows.
    /// </summary>
    /// <remarks>
    ///     The concurrency cap asks "are there already N of them", so counting past N is work nobody reads.
    ///     <c>Pending</c> is NOT counted, which makes this a different question from "does a live run pin this
    ///     definition": Pending is the queue admission draws from, so counting it would count the run asking to start
    ///     against its own admission, a cap of one would admit nothing, and a Pending backlog would block every start.
    /// </remarks>
    Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>By node key, which is the node run's identity within its run — there is one row per <c>(run, node key)</c>.</summary>
    Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Answers a pause: the node run moves <c>WaitingForApproval → Succeeded</c> carrying its decision columns and
    ///     its composed output, and a <c>gate.decided</c> event is appended, in one transaction.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means the conditional write matched no row — the pause was decided or moved between
    ///     the caller's read and this write. Re-read and answer from what the row now says; it is not an error, which
    ///     is why it is not an exception.
    /// </remarks>
    Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node run one operation id already decided on this run, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Run-WIDE, which is the scope the filtered unique index enforces: an id reused on a second pause of the same
    ///     run has to be seen as the conflict it is, before a write turns it into a unique-index violation instead.
    /// </remarks>
    Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default);

    /// <summary><paramref name="afterSeq" /> is an EXCLUSIVE lower bound, so a client replaying from what it rendered sees nothing twice.</summary>
    Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node runs a restart has to judge: everything left <c>Queued</c> or <c>Running</c>, read without writing.
    /// </summary>
    /// <remarks>
    ///     Exactly that set and no wider — <c>WaitingForApproval</c> is a durable human wait that a restart does not
    ///     invalidate, and <c>Pending</c> was never dispatched.
    /// </remarks>
    Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restart recovery, as ONE transaction: the <c>Queued</c> and <c>Running</c> node runs collapse back to
    ///     <c>Pending</c>, each with a <c>node.interrupted</c> event, with the caller's repairs in the same commit.
    /// </summary>
    /// <remarks>
    ///     Runs auto-resume, so no run row is touched, and a host that dies mid-recovery leaves the rows as it found
    ///     them. The NAME is a misnomer inherited from the development-workflow original — the set is
    ///     <c>Queued ∪ Running</c> — kept so the two modules' recovery paths read alike. Only rows whose live state
    ///     still matches their verdict are collapsed; a non-null <paramref name="unjudged" /> makes this the LAST pass
    ///     and settles the rest, decided against the row in front of it where no snapshot can be stale.
    /// </remarks>
    Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default);
}

public sealed class GraphWorkflowNotFoundException : InvalidOperationException
{
    public GraphWorkflowNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
///     Both ways a definition write can lose: a stale <c>version</c> on an update, and a delete refused while a live
///     run pins the definition.
/// </summary>
/// <remarks>
///     One type, because from the client's side they are one story — somebody else got there first. Maps to a 409
///     through <c>ConflictExceptionHandler</c>.
/// </remarks>
public sealed class GraphWorkflowDefinitionConflictException : InvalidOperationException
{
    public GraphWorkflowDefinitionConflictException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>
///     The rejection channel for a run write the store refuses: a forbidden move, a stale <c>ExpectedVersion</c>, or
///     the concurrency token losing a race.
/// </summary>
/// <remarks>
///     One story from the caller's side — the row is not what you thought it was, so re-read it.
///     The store deliberately does not judge LEGALITY: the transition tables live in the Application layer, which
///     this assembly may not reference. What is checked here is what the database can see — the version, the identity
///     of the rows, and the unique indexes.
/// </remarks>
public sealed class GraphWorkflowInvalidTransitionException : InvalidOperationException
{
    public GraphWorkflowInvalidTransitionException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
