namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The durable substrate for development workflows: one monotonic sequence per run, an append-only event log, and
///     optimistic concurrency on the run row.
/// </summary>
/// <remarks>
///     Every mutation runs in one transaction that loads the run row, checks <c>ExpectedVersion</c> (unless it is
///     <see cref="DevWorkflowVersions.Any" />), allocates sequences, appends one event and bumps the version. A
///     non-null operation id resolves query-first, before AND inside it, so a replay cannot double-append and a racing
///     writer blocks on the writer lock, then returns the recorded result. Legal transitions are the runtime's: the
///     store offers <see cref="DevWorkflowInvalidTransitionException" /> and enforces only what the database can — one live run per item, one decision per node-run attempt, one owner per session.
/// </remarks>
public interface IDevWorkflowStore
{
    Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The work-item page with each item's latest run and that run's node counters. Two queries total regardless of
    ///     row count — the page, then one pass over the node-runs of the listed runs — never one per row.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null, CancellationToken cancellationToken = default);

    Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the work item and every row below it in explicit dependency order, and answers what went —
    ///     including the work sessions and runs whose EXTERNAL state the caller must now release.
    /// </summary>
    /// <remarks>
    ///     A run's own children cascade, but the work sessions carry no foreign key to the work item, so this path is
    ///     the only thing that reaches them, and the caller cannot be told what to release unless the rows are
    ///     enumerated on the way out. Refuses with <see cref="DevWorkflowRunInFlightException" /> while any of the
    ///     item's runs is non-terminal, checked inside the transaction so a run that starts mid-delete still wins; the
    ///     caller learns what to release only from a COMMITTED delete, so a refusal never arrives too late.
    /// </remarks>
    Task<DevWorkflowWorkItemDeletion> DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Never loads <c>graph_json</c>: the node count is the denormalized column, not a parse.</summary>
    Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Delete is an archive: runs that reference the definition, in flight or historical, are unaffected.</summary>
    Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default);

    /// <summary>Never loads <c>body</c>: the list draws names and scopes, and the body is the encrypted column.</summary>
    Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A HARD delete: a rule set is not referenced by a foreign key, and every node-run that applied one recorded
    ///     its <c>{id, name, contentSha256}</c> at materialization, so the audit survives the document.
    /// </summary>
    Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The resolver's read, at run start and at each materialization.
    /// </summary>
    /// <remarks>
    ///     Bodies included: the resolver SNAPSHOTS the text it matched onto the node run, so what a node was given can
    ///     never be re-derived from a document that has since moved on. ponytail: a full scan of the enabled rows with
    ///     no cache — the scope is a JSON document with no column to index on, the working set is a handful of rows on
    ///     a single-operator node, and the call happens once per run start and once per expansion, not per node run.
    ///     Add a projected per-axis index table only if the rule count ever reaches the hundreds.
    /// </remarks>
    Task<IReadOnlyList<DevWorkflowRuleSetSnapshot>> ListEnabledRuleSetsAsync(CancellationToken cancellationToken = default);

    Task<DevWorkflowRunSnapshot> StartRunAsync(StartDevWorkflowRunCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowRunSnapshot>> ListRunsAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run list, newest first, with each run's definition name and node counters. Two queries whatever the row
    ///     count — the page, then one grouped pass over the node-runs of the listed runs — never one per row.
    /// </summary>
    /// <remarks>
    ///     Both filters are optional: the work-item detail passes an id to embed that item's runs, and the run list
    ///     page passes a status. <see cref="ListRunsAsync" /> answers the same rows without the joins, for the
    ///     dispatcher's sweep, which needs neither name nor counters.
    /// </remarks>
    Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node-runs a restart has to judge: everything left <c>Queued</c> or <c>Running</c>, read without writing.
    /// </summary>
    /// <remarks>
    ///     The caller decides what each one costs — an attempt, a human, nothing — and hands those decisions back to
    ///     <see cref="ReconcileNonTerminalNodeRunsAsync" />, which is where they are committed.
    /// </remarks>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restart recovery, as ONE transaction: the node-runs the host left <c>Queued</c> or <c>Running</c> collapse
    ///     back to <c>Pending</c>, each with one <c>node.interrupted</c> event.
    /// </summary>
    /// <remarks>
    ///     <c>WaitingForApproval</c> and <c>Blocked</c> are durable human-wait states and survive untouched, so a
    ///     second pass is empty. <paramref name="verdicts" /> commit in the same transaction as the collapse, ONLY for
    ///     rows whose live state still matches, and a non-null <paramref name="unjudged" /> makes this the caller's
    ///     LAST pass by blocking what it could not judge. Why each of those three is so:
    ///     docs/wiki/08-data-and-persistence.md ("Dev-workflow restart recovery").
    /// </remarks>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A cross-node retry route, as ONE transaction: the <c>node.retry.routed</c> event and every node-run reset it
    ///     implies commit together or not at all.
    /// </summary>
    /// <remarks>
    ///     Not a loop of <see cref="TransitionNodeRunAsync" />, and the difference is correctness: a route resets the whole subtree, and a crash
    ///     mid-loop leaves some rows <c>Pending</c> while the rest keep answers the re-run invalidates — which nothing
    ///     reconciles, since recovery only judges <c>Queued</c>/<c>Running</c> rows. All or nothing leaves either the
    ///     recorded failure, which the dispatcher re-routes next sweep, or the reset subtree. <c>Route</c>'s operation
    ///     id governs the command; quiescing the superseded lane work is the CALLER's, as no transaction can undo it.
    /// </remarks>
    Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Takes no <c>sinceSequence</c>: a node-run's sequence is its insert order, not a change watermark, so a
    ///     status-only change would be invisible to such a feed. Status changes come from the event log.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every work session a node run currently owns, across all runs — the set a workflow-kind session must belong
    ///     to in order to be reachable at all.
    /// </summary>
    /// <remarks>
    ///     One distinct-projection query, for the startup sweep that deletes the sessions nothing points at: a session
    ///     created for a node run whose attach never committed is invisible to a work-item delete and refused to every
    ///     external caller, so this is the only thing that can find it.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run driving a Development task, or null for a task no workflow owns — the reverse of the pointer a
    ///     <c>DevTask</c> node run stamps, read over that column's own index.
    /// </summary>
    /// <remarks>
    ///     CONTRACT: the LATEST such node run answers. A task can be named by more than one node run over its life (a
    ///     re-run of the same definition drives the same task), and the question this exists to answer — where does
    ///     the approval for this task live NOW — has exactly one useful answer.
    /// </remarks>
    Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same question for a whole task list, in ONE query. The project page asks it once per task otherwise,
    ///     which is a round trip per row on the one read that always has every row.
    /// </summary>
    /// <remarks>
    ///     Same CONTRACT as the single-task read: the latest node run naming a task answers for it, and a task no
    ///     workflow owns is simply absent from the dictionary.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records an artifact, resolving its lineage by <c>(run, producing node key, name)</c>.
    /// </summary>
    /// <remarks>
    ///     The same node key appending again versions the same lineage, and materialized siblings under one template
    ///     get distinct ones. Deleting the superseded version's bytes is the caller's job.
    /// </remarks>
    Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default);

    /// <summary>Pure DB work over the recorded uses; it flags dependents and never regenerates anything.</summary>
    Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of a run's artifacts. The cursor is append-correct only.
    /// </summary>
    /// <remarks>
    ///     An artifact's sequence is allocated at insert and never re-stamped, so a <c>sinceSequence</c> page carries
    ///     every artifact that has appeared since and no staleness flip that has happened since. Staleness mutations
    ///     are announced on the event feed as <c>artifact.stale.marked</c> and observed by refetching the artifact,
    ///     never by advancing this cursor.
    /// </remarks>
    Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> RecordDecisionAsync(RecordDevWorkflowDecisionCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The idempotent-replay read for a decision. A repeated POST has to return the same <em>body</em>, not just the
    ///     same run state, and the mutation result carries no decision id, subject or decided-at.
    /// </summary>
    Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The <c>event_type</c> this operation has already committed against this run, or <see langword="null" />
    ///     when it has not run.
    /// </summary>
    /// <remarks>
    ///     Exposed because a caller has to ask BEFORE judging legality: a command that committed and was then retried
    ///     is a replay, and re-checking the status it already changed answers a conflict to a caller who did nothing
    ///     wrong. It answers the TYPE, not merely "yes", because an operation id names one ACT: a caller reusing a
    ///     pause's id on a cancel is replaying nothing. It is deliberately not the recorded
    ///     <see cref="DevWorkflowMutationResult" />, which a reader — and the reflection that holds the publishing decorator to every mutation declared here — cannot tell from a committed one.
    /// </remarks>
    Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);
}
