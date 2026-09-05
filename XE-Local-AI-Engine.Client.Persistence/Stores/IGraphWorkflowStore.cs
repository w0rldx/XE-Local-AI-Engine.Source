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

/// <summary>
///     The sentinel <see cref="Any" /> version. The dispatcher moves node-run status while a human HTTP action may be
///     writing a cancel on the same run, so an ordinary status move — which has no lost update to protect against,
///     because a replayed tick re-derives the same answer from unchanged rows — passes <see cref="Any" /> and never
///     loses that race. A run-level write that must NOT lose it passes the version it read.
/// </summary>
public static class GraphWorkflowVersions
{
    public const long Any = -1;
}

/// <summary>
///     One run row with its encrypted columns decoded to text. <see cref="GraphJson" /> is the copy pinned at start:
///     the definition row may be edited, or deleted, without changing what this run executes.
/// </summary>
public sealed record GraphWorkflowRunSnapshot(
    Guid Id,
    Guid RequestId,
    Guid DefinitionId,
    int DefinitionVersion,
    string GraphHash,
    GraphWorkflowRunStatus Status,
    GraphWorkflowFailureClass FailureClass,
    string GraphJson,
    string? InputJson,
    string? OutputJson,
    long Seq,
    long Version,
    long? CancelRequestedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long CreatedAtUtc);

/// <summary>One entry of a run's append-only change log. <see cref="Seq" /> is its order as well as its watermark.</summary>
public sealed record GraphWorkflowRunEventSnapshot(
    Guid Id,
    Guid RunId,
    long Seq,
    string EventType,
    string? NodeKey,
    string? DetailJson,
    long CreatedAtUtc);

/// <summary>
///     What one mutation committed: the run it belongs to and the watermark its event took.
///     <para>
///         Deliberately NOT the post-commit version. A caller that needs one re-reads the run immediately before its
///         next run-level write, because this tick's own node-run writes have already moved it — a version carried out
///         of here would be stale by the time anything used it.
///     </para>
/// </summary>
public sealed record GraphWorkflowMutationResult(Guid RunId, long Sequence);

/// <summary>
///     One node run to create at start. There is one per node of the pinned graph and they are all <c>Pending</c>:
///     every node run of a graph workflow exists from the moment the run does, which is what lets admission be a pure
///     function of rows.
///     <para>
///         No attempt cap travels here. The node's <c>maxAttempts</c> lives in the pinned graph and the retry stage
///         reads it from there, so a column beside it could only ever disagree with the document the run executes.
///     </para>
/// </summary>
public sealed record GraphWorkflowNodeRunSeed(Guid NodeRunId, string NodeKey, GraphWorkflowNodeKind Kind, string? InputJson = null);

/// <summary>
///     A run start, as ONE transaction: the run row, one <c>Pending</c> node run per graph node, and the
///     <c>run.created</c> event.
///     <para>
///         The definition's existence and version are re-checked INSIDE that transaction — the obligation
///         <see cref="IGraphWorkflowStore.DeleteDefinitionAsync" /> names. A start that read the definition in one
///         transaction and inserted here in another could pin a definition a delete has already removed.
///     </para>
/// </summary>
public sealed record StartGraphWorkflowRunCommand(
    Guid RunId,
    Guid RequestId,
    Guid DefinitionId,
    int DefinitionVersion,
    string GraphHash,
    string GraphJson,
    string? InputJson,
    IReadOnlyList<GraphWorkflowNodeRunSeed> NodeRuns);

/// <summary>
///     A run status move.
///     <para>
///         <see cref="SanitizedReason" /> has no column on the run row and is not meant to: it travels into the event's
///         detail, where a reader following the log finds it beside the move it explains.
///     </para>
///     <para>
///         The cancel-requested instant is deliberately NOT a member: the store stamps it from its own
///         <see cref="TimeProvider" /> on the move to <c>Cancelling</c>, like every other timestamp on these rows, so a
///         caller's clock cannot disagree with the row's.
///     </para>
/// </summary>
public sealed record TransitionGraphWorkflowRunCommand(
    Guid RunId,
    long ExpectedVersion,
    GraphWorkflowRunStatus TargetStatus,
    GraphWorkflowFailureClass? FailureClass = null,
    string? SanitizedReason = null,
    string? OutputJson = null);

/// <summary>
///     A node-run status move. <see cref="IncrementAttempt" /> is the retry-in-place path; the row is never duplicated,
///     and the per-attempt history lives in the event log.
///     <para>
///         <see cref="QueueReason" /> and <see cref="TerminalReason" /> both land in the row's ONE reason column: why a
///         row is queued and why it ended are the same question asked at different moments, and the row is only ever in
///         one of those states. A move back to <c>Pending</c> clears it, because a re-attempt must not report the
///         previous attempt's outcome while it runs.
///     </para>
///     <para>
///         <see cref="EventType" /> overrides the token derived from <see cref="TargetStatus" />, for the one move the
///         status alone cannot express — a re-attempt, which is <c>node.retried</c> rather than the generic
///         <c>Pending</c> collapse the reconciler writes. <see cref="DetailJson" /> replaces the detail this move would
///         otherwise derive from <see cref="TerminalReason" />, for that same move: it has cleared the failure it is
///         re-attempting because of.
///     </para>
/// </summary>
public sealed record TransitionGraphWorkflowNodeRunCommand(
    Guid RunId,
    Guid NodeRunId,
    long ExpectedVersion,
    GraphWorkflowNodeRunStatus TargetStatus,
    string? QueueReason = null,
    string? OutputJson = null,
    string? InputJson = null,
    GraphWorkflowFailureClass? FailureClass = null,
    string? TerminalReason = null,
    string? DetailJson = null,
    Guid? InvocationId = null,
    GraphWorkflowDecisionKind? PendingDecisionKind = null,
    bool IncrementAttempt = false,
    string? EventType = null);

public sealed record AppendGraphWorkflowEventCommand(
    Guid RunId,
    long ExpectedVersion,
    string EventType,
    string? NodeKey = null,
    string? DetailJson = null);

/// <summary>
///     One row a host death stranded, carrying what the runtime needs to judge it without a follow-up read per row.
///     <see cref="Status" /> is what the row held BEFORE the collapse — what it was doing is the useful fact, since
///     where it lands is always <c>Pending</c> unless a repair moves it further.
/// </summary>
public sealed record GraphWorkflowReconciledNodeRun(
    Guid NodeRunId,
    Guid RunId,
    string NodeKey,
    GraphWorkflowNodeKind Kind,
    GraphWorkflowNodeRunStatus Status,
    int Attempt);

/// <summary>
///     One judged node run: the row as the caller observed it, and what to do with it once the collapse has confirmed
///     it is still that row. A verdict is only true of the state it was decided from, so a row that moved under the
///     caller is left exactly as it is rather than repaired from stale evidence.
///     <para>
///         Every command in <see cref="Repairs" /> MUST carry <see cref="GraphWorkflowVersions.Any" />: the collapse
///         bumps its run's version once per stranded row before any repair is applied, so a repair naming the version
///         its caller read is stale by construction and fails the whole recovery transaction rather than its own row.
///     </para>
/// </summary>
public sealed record GraphWorkflowNodeRunVerdict(
    Guid NodeRunId,
    GraphWorkflowNodeRunStatus ObservedStatus,
    int ObservedAttempt,
    IReadOnlyList<TransitionGraphWorkflowNodeRunCommand> Repairs);

/// <summary>
///     Turns a reconciliation into a SETTLING pass: every stranded node run no verdict matched is failed rather than
///     left where it is. Pass it on the last pass only — walking away strands a row nothing downstream picks up again,
///     and v1 has no <c>Blocked</c> state to park it in.
/// </summary>
public sealed record GraphWorkflowUnjudgedNodeRunSettlement(GraphWorkflowFailureClass FailureClass, string SanitizedReason);

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
///     <para>
///         With ONE exception: <see cref="NodeCount" /> and <see cref="GraphJson" /> travel TOGETHER or not at all, and
///         either one without the other is refused with an <see cref="ArgumentException" />. The count is denormalized
///         so the definition list never decrypts a blob, which makes both halves of that the same lie: a new graph
///         beside the old graph's count, or a new count beside the graph it was not taken from. The count is derived,
///         never edited. <see cref="SchemaVersion" /> stays optional: this node understands one schema version and the
///         parser refuses every other, so a graph that reached the store IS that version and the stored value already
///         says so.
///     </para>
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
    ///     this definition is still live — checked INSIDE the transaction. Terminal runs are unaffected: each pinned
    ///     its own copy of the graph at start, so history survives the row.
    ///     <para>
    ///         The transaction makes delete-vs-start safe only UNDER A PRECONDITION the run store owes: run start must
    ///         re-read the definition's existence and version inside the SAME transaction that inserts the run row. A
    ///         start that reads the definition first and inserts afterwards, in a second transaction, can insert a run
    ///         pinned to a definition this delete has already removed — the live-run count here would have seen
    ///         nothing, because the run did not exist yet. Nothing in S0 starts runs; S1's run store carries the
    ///         obligation.
    ///     </para>
    /// </summary>
    Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a run: the run row, one <c>Pending</c> node run per seed and the <c>run.created</c> event, in one
    ///     transaction that re-reads the definition first.
    ///     <para>
    ///         <b>The insert IS the idempotency guarantee.</b> A caller's check-then-act on
    ///         <see cref="FindRunByRequestAsync" /> can be raced by a genuinely concurrent identical start, so this
    ///         inserts first and catches the unique-index violation on <c>request_id</c>: on that catch it rolls back,
    ///         re-reads by request id and answers with the run that WON. The index is the lock; no application-level
    ///         gate is added on top of it.
    ///     </para>
    /// </summary>
    Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>The run a caller-minted request id already started, or <see langword="null" />. Never throws for an unknown id.</summary>
    Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Newest first, optionally filtered by status.</summary>
    Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    ///     How many runs are still live, counted no further than <paramref name="probeLimit" /> rows. The question the
    ///     concurrency cap asks is "are there already N of them", so counting past N is work nobody reads.
    /// </summary>
    Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>By node key, which is the node run's identity within its run — there is one row per <c>(run, node key)</c>.</summary>
    Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default);

    /// <summary><paramref name="afterSeq" /> is an EXCLUSIVE lower bound, so a client replaying from what it rendered sees nothing twice.</summary>
    Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node runs a restart has to judge: everything left <c>Queued</c> or <c>Running</c>, read without writing
    ///     anything. Exactly that set and no wider — <c>WaitingForApproval</c> is a durable human wait that a restart
    ///     does not invalidate, and <c>Pending</c> was never dispatched.
    /// </summary>
    Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restart recovery, as ONE transaction. Runs auto-resume, so no run row is touched; only the <c>Queued</c> and
    ///     <c>Running</c> node runs collapse back to <c>Pending</c>, each with a <c>node.interrupted</c> event, and the
    ///     caller's per-row repairs are applied in the same commit. A host that dies mid-recovery leaves the rows as it
    ///     found them.
    ///     <para>
    ///         The NAME is inherited from the development-workflow original and is a misnomer there too: the set is
    ///         <c>Queued ∪ Running</c>, never "non-terminal". It is kept so the two modules' recovery paths read alike.
    ///     </para>
    ///     <para>
    ///         Only rows whose live state still matches their verdict are collapsed; a row that moved under the caller
    ///         is left for the next pass. A non-null <paramref name="unjudged" /> makes this the LAST pass and settles
    ///         whatever is left, decided against the row in front of it where no snapshot can be stale.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default);
}

public sealed class GraphWorkflowNotFoundException(string message) : InvalidOperationException(message);

/// <summary>
///     Both ways a definition write can lose, under one type because from the client's side they are one story —
///     somebody else got there first: a stale <c>version</c> on an update, and a delete refused while a live run pins
///     the definition. Maps to a 409 through <c>ConflictExceptionHandler</c>.
/// </summary>
public sealed class GraphWorkflowDefinitionConflictException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

/// <summary>
///     The rejection channel for a run write the store refuses: a move the state machine forbids, a stale
///     <c>ExpectedVersion</c>, and the concurrency token losing a race are all one story from the caller's side —
///     the row is not what you thought it was, so re-read it.
///     <para>
///         The store deliberately does not judge LEGALITY: the transition tables live in the Application layer, which
///         this assembly may not reference. What is checked here is what the database can see — the version, the
///         identity of the rows, and the unique indexes.
///     </para>
/// </summary>
public sealed class GraphWorkflowInvalidTransitionException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);
