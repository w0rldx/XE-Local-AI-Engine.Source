namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The durable substrate for development workflows: one monotonic sequence per run, an append-only event log, and
///     optimistic concurrency on the run row.
///     <para>
///         Every mutation runs in one transaction that loads the run row, checks <c>ExpectedVersion</c> (unless it is
///         <see cref="DevWorkflowVersions.Any" />), allocates sequence values from the run's counter, appends one event,
///         and bumps the version. A non-null operation id resolves query-first: an operation already recorded returns
///         without writing, so a replayed step cannot double-append. The check runs both before and inside the
///         transaction, and the inner one is what makes a genuine race safe: a second writer blocks on SQLite's writer
///         lock, then sees the recorded operation and returns that result rather than an exception.
///     </para>
///     <para>
///         Legal state transitions are the runtime's to enforce, not this store's; this store provides
///         <see cref="DevWorkflowInvalidTransitionException" /> as the rejection channel and enforces only what the
///         database can — one live run per work item, one decision per node-run attempt, one owner per work session.
///     </para>
/// </summary>
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
    ///     Removes the work item and every row below it in explicit dependency order, and answers what went — including
    ///     the work sessions and runs whose EXTERNAL state the caller must now release. The node connection runs without
    ///     <c>PRAGMA foreign_keys</c>, so the declared cascades never fire and the order is the only thing that keeps
    ///     the delete complete.
    ///     <para>
    ///         Refuses with <see cref="DevWorkflowRunInFlightException" /> while any of the item's runs is non-terminal,
    ///         checked inside the transaction so a run that starts mid-delete still wins. The caller learns what to
    ///         release only from a delete that COMMITTED, so a refusal can never arrive after the transcripts it was
    ///         protecting have already been destroyed.
    ///     </para>
    /// </summary>
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
    ///     The resolver's read, at run start and at each materialization. Bodies included: the resolver SNAPSHOTS the
    ///     text it matched onto the node run, so what a node was given can never be re-derived from a document that has
    ///     since moved on.
    ///     <para>
    ///         ponytail: a full scan of the enabled rows with no cache. The scope is a JSON document with no column to
    ///         index on, the working set is a handful of rows on a single-operator node, and the call happens once per
    ///         run start and once per expansion — not per node run. Add a projected per-axis index table only if the
    ///         rule count ever reaches the hundreds.
    ///     </para>
    /// </summary>
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
    ///     <para>
    ///         Both filters are optional: the work-item detail passes an id to embed that item's runs, and the run list
    ///         page passes a status. <see cref="ListRunsAsync" /> answers the same rows without the joins, for the
    ///         dispatcher's sweep, which needs neither name nor counters.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node-runs a restart has to judge: everything left <c>Queued</c> or <c>Running</c>, read without writing
    ///     anything. The caller decides what each one costs — an attempt, a human, nothing — and hands those decisions
    ///     back to <see cref="ReconcileNonTerminalNodeRunsAsync" />, which is where they are committed.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restart recovery, as ONE transaction. Runs auto-resume, so no run-level status moves; only node-runs the host
    ///     left <c>Queued</c> or <c>Running</c> collapse back to <c>Pending</c> so the dispatcher can re-admit them, each
    ///     with one <c>node.interrupted</c> event. <c>WaitingForApproval</c> and <c>Blocked</c> are durable human-wait
    ///     states and survive untouched. Idempotent by construction: a second pass finds none of those states and returns
    ///     empty.
    ///     <para>
    ///         <paramref name="verdicts" /> carry the caller's per-row decisions — an attempt spent, a human needed —
    ///         applied IN ORDER inside the same transaction as the collapse, because a recovery that commits the collapse
    ///         alone is one the next boot cannot finish: those rows read as ordinary <c>Pending</c> and would be re-run
    ///         with no attempt or budget accounting at all. Committing both together makes recovery all-or-nothing, so
    ///         any number of crashes during startup still repairs every interrupted node-run exactly once.
    ///     </para>
    ///     <para>
    ///         ONLY the rows whose live state still matches their verdict are collapsed. A stranded row with no verdict,
    ///         or one whose status, attempt or work session moved since the verdict was decided, is left untouched for
    ///         the caller's next pass — this is what makes the method safe against a writer the caller did not expect,
    ///         such as a second process sharing the database. Repairs run under
    ///         <see cref="DevWorkflowVersions.Any" />: the run's version has by then moved by one event per collapsed
    ///         row, and the per-row match is the check that matters here.
    ///     </para>
    ///     <para>
    ///         A non-null <paramref name="unjudged" /> makes this the caller's LAST pass: the rows it could not judge are
    ///         blocked for a human rather than left, decided against the live row inside this transaction and so immune
    ///         to the drift that stranded them in the first place. Pass it when walking away is worse than a human wait
    ///         — which it is at startup, because nothing downstream picks a stranded row up again.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A cross-node retry route, as ONE transaction: the <c>node.retry.routed</c> event and every node-run reset it
    ///     implies commit together or not at all.
    ///     <para>
    ///         Not a loop of <see cref="TransitionNodeRunAsync" /> calls, and the difference is a correctness one. A
    ///         route resets the whole subtree under the node it re-runs; a crash part-way through that loop left some
    ///         of those rows <c>Pending</c> while the rest kept the answers the re-run is about to invalidate — an
    ///         already-executed apply, an already-answered gate. Nothing reconciles that afterwards: startup recovery
    ///         only judges rows left <c>Queued</c> or <c>Running</c>, so a <c>Pending</c> row under <c>Succeeded</c>
    ///         ancestors is re-dispatched as if fresh and the run completes on the stale evidence beside it. All or
    ///         nothing means a crash leaves either the failure still recorded, which the dispatcher re-derives and
    ///         re-routes on its next sweep, or the fully reset subtree.
    ///     </para>
    ///     <para>
    ///         The operation id on <c>Route</c> is the whole command's: a replay answers the recorded result and writes
    ///         nothing. Quiescing the live lane work the resets supersede is the CALLER's, and belongs before this —
    ///         stopping a session is not something a transaction can roll back.
    ///     </para>
    /// </summary>
    Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Takes no <c>sinceSequence</c>: a node-run's sequence is its insert order, not a change watermark, so a
    ///     status-only change would be invisible to such a feed. Status changes are observed through the event log.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every work session a node run currently owns, across all runs — the set a workflow-kind session must belong
    ///     to in order to be reachable at all.
    ///     <para>
    ///         One distinct-projection query, for the startup sweep that deletes the sessions nothing points at: a
    ///         session created for a node run whose attach never committed is invisible to a work-item delete and
    ///         refused to every external caller, so this is the only thing that can find it.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run driving a Development task, or null for a task no workflow owns — the reverse of the pointer a
    ///     <c>DevTask</c> node run stamps, read over that column's own index.
    ///     <para>
    ///         CONTRACT: the LATEST such node run answers. A task can be named by more than one node run over its life
    ///         (a re-run of the same definition drives the same task), and the question this exists to answer — where
    ///         does the approval for this task live NOW — has exactly one useful answer.
    ///     </para>
    /// </summary>
    Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same question for a whole task list, in ONE query. The project page asks it once per task otherwise,
    ///     which is a round trip per row on the one read that always has every row.
    ///     <para>
    ///         Same CONTRACT as the single-task read: the latest node run naming a task answers for it, and a task no
    ///         workflow owns is simply absent from the dictionary.
    ///     </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records an artifact, resolving its lineage by <c>(run, producing node key, name)</c>: the same node key
    ///     appending again versions the same lineage, and materialized siblings under one template get distinct ones.
    ///     Deleting the superseded version's bytes is the caller's job.
    /// </summary>
    Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default);

    /// <summary>Pure DB work over the recorded uses; it flags dependents and never regenerates anything.</summary>
    Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The artifact cursor is append-correct only: an artifact's sequence is allocated at insert and never
    ///     re-stamped, so a <c>sinceSequence</c> page carries every artifact that has appeared since and no staleness
    ///     flip that has happened since. Staleness mutations are announced on the event feed as
    ///     <c>artifact.stale.marked</c> and observed by refetching the artifact, never by advancing this cursor.
    /// </summary>
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
    ///     The <c>event_type</c> this operation has already committed against this run, or <see langword="null" /> when
    ///     it has not run.
    ///     <para>
    ///         Every mutation resolves the same fact internally, so a replayed command is safe wherever it lands. It is
    ///         exposed because a caller has to ask BEFORE judging legality: a command that committed and was then
    ///         retried is a replay, and re-checking the status it has already changed would answer a conflict to a
    ///         caller who did nothing wrong.
    ///     </para>
    ///     <para>
    ///         It answers the event TYPE rather than merely "yes", because an operation id names one ACT and not one
    ///         run: a caller that reuses a pause's id on a cancel is replaying nothing, and a bare yes would report
    ///         that cancel as done without anything having been cancelled. The caller compares what was recorded
    ///         against the verb it is serving.
    ///     </para>
    ///     <para>
    ///         Deliberately not the recorded <see cref="DevWorkflowMutationResult" />: a read handing back a mutation's
    ///         result is indistinguishable — to a reader, and to the reflection that holds the publishing decorator to
    ///         every mutation this interface declares — from having committed one.
    ///     </para>
    /// </summary>
    Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);
}
