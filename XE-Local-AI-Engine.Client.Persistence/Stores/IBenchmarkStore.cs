namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IBenchmarkStore
{
    /// <summary>
    ///     Creates the project and, with a <paramref name="judgePolicy" />, activates its judge in the SAME
    ///     transaction: a project that persisted with judging off because a second transaction never ran is one an
    ///     operator can only retry into a duplicate. <paramref name="initialItems" /> is created in that same
    ///     transaction for the same reason — so a project never exists without at least one question to ask, and no
    ///     read path has to invent one.
    /// </summary>
    /// <param name="initialItems">
    ///     The project's task items. Empty or <see langword="null" /> leaves the project item-less, which is what a
    ///     caller written before task items existed does; <see cref="GetOrCreateItemsAsync" /> materializes item 0 for
    ///     it on first touch.
    /// </param>
    Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicy = null,
        IReadOnlyList<BenchmarkTaskItemInput>? initialItems = null,
        CancellationToken cancellationToken = default);

    Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Edits an unfrozen project and, with a <paramref name="judgePolicyChange" />, applies that judge change in
    ///     the SAME transaction, for the same reason <see cref="CreateProjectAsync" /> does.
    /// </summary>
    Task<BenchmarkProjectRecord> UpdateProjectAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicyChange = null,
        CancellationToken cancellationToken = default);

    Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Every task item of a project — generators included — ordered by <see cref="BenchmarkTaskItemRecord.Index" />.</summary>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListTaskItemsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The project's task items, materializing item 0 from <see cref="BenchmarkProjectRecord.CoreTaskJson" /> when
    ///     it has none. This is a LEGACY path only: a project created after task items existed gets them in the same
    ///     transaction as itself, so the write-on-a-read-path is not reachable from anything an operator can newly
    ///     create.
    ///     <para>
    ///         It runs inside the normal EF write path so both encryption interceptors fire — which is exactly why a
    ///         migration cannot do this instead: it has no node key, and the prompt is a required encrypted blob bound
    ///         to its own item's id. Idempotent under the unique (project, index) index: a race is a constraint
    ///         violation this catches and re-reads, not a second item 0.
    ///     </para>
    ///     <para>
    ///         It deliberately leaves <see cref="BenchmarkProjectRecord.TaskItemSetHash" /> null. Materializing item 0
    ///         changes nothing about what the project asks, so it must not move the hash every historical run is
    ///         compared against — that would unrank a whole project's history for a bookkeeping write.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> GetOrCreateItemsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends one task item and recomputes the project's item-set hash. A changed set hash resets the rank
    ///     cohort, through the same path a judge-policy activation uses: the project score is a mean over the item
    ///     set, so changing the set changes what the score means.
    /// </summary>
    /// <param name="children">
    ///     The leaf cases a generator item expands into, written in the SAME transaction as the generator. A case is
    ///     an ordinary item with its own id, so every cap, every hash and the export reach it without knowing what
    ///     generated it — and a generator never exists, even for one commit, without the cases it promises.
    /// </param>
    Task<BenchmarkTaskItemRecord> CreateTaskItemAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rewrites one task item's payloads, bumping its <see cref="BenchmarkTaskItemRecord.Revision" /> and
    ///     recomputing its <see cref="BenchmarkTaskItemRecord.InputHash" /> — which is what makes every stored answer
    ///     to the OLD instance identifiable as an answer to a question that no longer exists.
    /// </summary>
    /// <param name="children">
    ///     A generator's cases, REGENERATED: the item's existing children are deleted and these written in their
    ///     place, inside the same transaction as the edit. Atomicity is the point — a case must never be left
    ///     describing parameters its generator no longer has.
    /// </param>
    Task<BenchmarkTaskItemRecord> UpdateTaskItemAsync(Guid projectId,
        Guid itemId,
        long expectedItemVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes one task item, and a generator's children before the generator itself — foreign keys are off on
    ///     this connection and no cascade fires, so that order IS the referential integrity. Deleting the last leaf is
    ///     refused: a project always asks at least one question.
    /// </summary>
    Task DeleteTaskItemAsync(Guid projectId, Guid itemId, long expectedItemVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Renumbers the project's items into the given order. Deliberately NOT a revision bump and NOT a cohort
    ///     reset: the index is a display position, it is absent from every hash, and a drag-and-drop must not unrank a
    ///     completed suite. <paramref name="orderedItemIds" /> must name exactly the project's current items, which is
    ///     also this call's concurrency check.
    /// </summary>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ReorderTaskItemsAsync(Guid projectId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts ONE run. Shorthand for a single-item <see cref="StartRunsAsync" /> against the command's own
    ///     <see cref="BenchmarkStartRunCommand.ExpectedProjectVersion" />.
    /// </summary>
    Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a whole group of runs ATOMICALLY: one transaction, one compare-and-swap against
    ///     <paramref name="expectedProjectVersion" />, one <c>project.Version += commands.Count</c>, and the runs plus
    ///     their primary work items inserted in the given order (which is the FIFO order they will execute in).
    ///     <para>
    ///         All-or-nothing is the point. Inserting a repeat group one run per transaction, each chaining its CAS on
    ///         its predecessor, let a concurrent writer land mid-group: the caller got a conflict and no ids while the
    ///         runs already inserted stayed queued and consumed the exclusive runtime. It also left a batch caller
    ///         unable to chain — the project version had moved by a number it was never told.
    ///     </para>
    ///     <para>
    ///         Every command must name the same project. A <see cref="BenchmarkStartRunCommand.FreezeCommitGuard" /> is
    ///         evaluated once per distinct guard instance, inside the same transaction.
    ///     </para>
    /// </summary>
    /// <returns>The created runs, in the order they were given.</returns>
    Task<IReadOnlyList<BenchmarkRunRecord>> StartRunsAsync(IReadOnlyList<BenchmarkStartRunCommand> commands,
        long expectedProjectVersion,
        CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     One page of a project's runs, newest first, carrying only what a summary needs. The encrypted payload
    ///     columns are NOT read: a list of runs must not decrypt every snapshot, output and receipt on the way to
    ///     rendering a table.
    /// </summary>
    /// <param name="modelContentFingerprint">Only runs of this exact model content, or null for all.</param>
    /// <param name="includeUnscored">False drops runs that carry no quality score at all.</param>
    Task<BenchmarkRunPage> ListRunsAsync(Guid projectId,
        int skip,
        int take,
        string? modelContentFingerprint = null,
        bool includeUnscored = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every run of a project in one call, ranked once. The paging overload recomputes the whole-project ranking —
    ///     a full scan plus a judge-view join across three more tables — for each page, and an export reads every page.
    ///     The default body pages through <see cref="ListRunsAsync" /> so a test double needs no extra member.
    /// </summary>
    Task<BenchmarkRunPage> ListAllRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        ListRunsAsync(projectId, skip: 0, int.MaxValue, modelContentFingerprint: null, includeUnscored: true, cancellationToken);

    /// <summary>
    ///     The project's measurement CELLS: one model, one KV type, one repeat of the whole task-item suite. A cell is
    ///     what ranks, so this is the shape a comparison reads — the per-run listing shows the same numbers one row at
    ///     a time and cannot say which items a cell is missing.
    /// </summary>
    Task<BenchmarkCellPage> ListCellsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>How many runs a project has, counted in the database.</summary>
    Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The project's ACTIVE work — queued or running — counted per kind, empty when the project is idle. A run's
    ///     <see cref="BenchmarkRunRecord.PrimaryStatus" /> does not answer this: a judged, measured or pairwise-compared
    ///     matrix keeps the single-consumer queue and the GPU busy long after every run of it reads
    ///     <see cref="BenchmarkPrimaryStatus.Succeeded" />.
    /// </summary>
    Task<IReadOnlyDictionary<BenchmarkWorkKind, int>> CountActiveWorkAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    /// <param name="primaryStopReason">
    ///     Why generation stopped, when the failure itself says so — <c>timeout</c> for a run the node cancelled at its
    ///     invocation budget. Null leaves the column untouched, so a failure that cannot explain itself records nothing
    ///     rather than guessing.
    /// </param>
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        string? primaryStopReason = null,
        CancellationToken cancellationToken = default) =>
        MarkPrimaryFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);

    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkPrimaryCancelledAsync(runId, expectedRunVersion, cancellationToken);

    Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enqueues one fidelity measurement of a run: a <c>Queued</c> attempt at the next sequence plus its work
    ///     item, in one transaction. Re-measuring inserts a NEW attempt rather than reusing the previous one, so the
    ///     numbers a KLD figure was measured against survive a corpus or chunk-count change.
    /// </summary>
    Task<Guid> EnqueueFidelityAsync(Guid runId, string kind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes a fidelity work item and its attempt, and — only when the attempt succeeded AND is the
    ///     highest-sequenced succeeded attempt of the run — refreshes the run's fidelity projection from it. A stale
    ///     re-measurement therefore cannot overwrite newer numbers, and a failed one leaves the previous numbers alone.
    /// </summary>
    Task<BenchmarkRunRecord> MarkFidelitySucceededAsync(BenchmarkFidelitySuccessCommand command, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkFidelityFailedAsync(Guid runId, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkFidelityCancelledAsync(Guid runId, long expectedWorkVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a claimed fidelity item — work item AND attempt — to <c>Queued</c>, carrying the reason it could not
    ///     proceed yet. For a blocker that clears itself, such as a base-logit file another process is still writing:
    ///     the work item pins <c>attempt = 1</c>, so terminalizing it as failed is terminal in the literal sense and
    ///     the "it will be retried" the operator was promised has nothing behind it. A no-op once the attempt is
    ///     terminal, so a requeue racing a completion cannot start a second measurement.
    /// </summary>
    Task<BenchmarkRunRecord> RequeueFidelityAsync(Guid runId, long expectedWorkVersion, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes a comparison work item and its comparison as failed. Keyed by queue sequence rather than by
    ///     run, because a comparison names TWO runs and "the run's comparison work item" is not a well-formed lookup.
    /// </summary>
    Task MarkComparisonFailedAsync(long queueSequence, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MarkComparisonFailedAsync" />
    Task MarkComparisonCancelledAsync(long queueSequence, long expectedWorkVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Everything the pairwise planner, the fitter and the pre-flight estimate read: the project's current cohort
    ///     scope, the runs eligible to be paired inside it, and the comparisons that already exist. One read, because
    ///     all three questions are about the same consistent moment.
    /// </summary>
    Task<BenchmarkPairwiseCohortState> GetPairwiseCohortAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts BOTH presentation orders of every slot that has no live-or-succeeded comparison yet, plus their work
    ///     items, in ONE transaction, and bumps the revision's <c>ComparisonSetVersion</c> in the same one. Idempotent:
    ///     a slot a concurrent caller already created violates the filtered unique index, and the violation is
    ///     swallowed — a half-created cohort is a cohort that never completes and therefore never publishes a score.
    /// </summary>
    /// <returns>How many comparison rows this call created.</returns>
    Task<int> EnsureComparisonsAsync(Guid projectId,
        IReadOnlyList<BenchmarkPairwiseSlot> slots,
        ReadOnlyMemory<byte>? judgeRuntimeJson,
        BenchmarkRunLaunchIntent? launchIntent,
        CancellationToken cancellationToken = default);

    /// <summary>One comparison by id, payloads included, or null when it is gone.</summary>
    Task<BenchmarkComparisonRecord?> GetComparisonAsync(Guid comparisonId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MarkJudgeLaunchReadyAsync" />
    Task<bool> MarkComparisonLaunchReadyAsync(Guid comparisonId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records one comparison's verdict and terminalizes its work item, bumping the cohort's
    ///     <c>ComparisonSetVersion</c> in the same transaction — inserting and terminalizing are the only two ways the
    ///     fitted set can change, so they are the only two places that bump it.
    /// </summary>
    Task MarkComparisonSucceededAsync(BenchmarkComparisonSuccessCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a fit as ONE pointer switch: deactivate the scope's active row and insert the new one, in one
    ///     transaction. A duplicate publication violates <c>ux_benchmark_pairwise_fits_key</c> and no-ops, and the
    ///     filtered active index makes two active fits in one scope unrepresentable rather than merely unlikely.
    /// </summary>
    /// <returns><see langword="true" /> when this call published; <see langword="false" /> when it was a duplicate.</returns>
    Task<bool> PublishPairwiseFitAsync(BenchmarkPairwiseFitCommand command, CancellationToken cancellationToken = default);

    /// <summary>The active fit of the project's current cohort scope, or null when nothing has published one.</summary>
    Task<BenchmarkPairwiseFitRecord?> GetActivePairwiseFitAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The median wall-clock seconds a completed judge attempt of this project took, or null when none has
    ///     completed. The pre-flight ETA is omitted rather than guessed when this is null.
    /// </summary>
    Task<double?> GetMedianJudgeDurationSecondsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Every project that currently points at a judge policy revision — the startup reconciliation's set.</summary>
    Task<IReadOnlyList<Guid>> ListJudgedProjectIdsAsync(CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeCancelledAsync(runId, expectedRunVersion, cancellationToken);

    /// <summary>
    ///     Records the primary phase's durable launch evidence: an insert-if-null write of the receipt/environment
    ///     columns, keyed by the immutable work item rather than by the run's mutable version. It never overwrites an
    ///     existing block and never changes any status, so it is safe to call before inference and again while
    ///     terminalizing. Returns <see langword="true" /> when this call wrote the block.
    /// </summary>
    /// <param name="workItemId">The claimed work item's queue sequence.</param>
    /// <param name="claimedWorkVersion">
    ///     The work-item version the caller claimed. The write is accepted while that work item is still
    ///     <c>Running</c> at exactly that version, or already <c>Cancelled</c> at its successor version (terminalizing
    ///     a work item bumps the version by exactly one) — the cancel-first ordering. Anything else is refused.
    /// </param>
    Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MarkPrimaryLaunchReadyAsync" />
    /// <param name="attemptId">The judge attempt this launch belongs to — judge evidence lives on the attempt, not the run.</param>
    /// <param name="judgeExecutionKey">
    ///     The rank-cohort key computed from this launch's effective evidence, or <see langword="null" /> when the
    ///     execution identity was incomplete (fail-closed: such an attempt is never ranked).
    /// </param>
    Task<bool> MarkJudgeLaunchReadyAsync(Guid attemptId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets or clears the operator's 0..100 override. <see langword="null" /> clears it; the judge score stays
    ///     visible beside it either way.
    /// </summary>
    Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int? score, long expectedRunVersion, CancellationToken cancellationToken = default);

    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        _ = await RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    /// <summary>
    ///     Removes a terminal run and everything that pointed at it: work items, judge attempts, fidelity attempts,
    ///     and every pairwise comparison it took part in on EITHER side. Refused while any of those is Queued or
    ///     Running — including a comparison whose canonical first run is the other one, which no work item names.
    ///     <para>
    ///         Deleting comparisons bumps the affected revisions' <c>ComparisonSetVersion</c> and deactivates the
    ///         project's active pairwise fits, because a fit whose fitted set names a deleted run ranks something that
    ///         is not there. The next planner pass re-fits whatever cohort is left.
    ///     </para>
    /// </summary>
    Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Points the project at the judge policy with this hash, creating the revision when the project has never
    ///     seen it, and resets that revision's rank cohort (reference key cleared, generation bumped) so only attempts
    ///     enqueued after this call can define it. Activating the hash the project is already on is a no-op that
    ///     resets nothing. Refused while any attempt of the project is Queued or Running, so a reset always covers the
    ///     complete eligible set.
    /// </summary>
    /// <param name="cohortAttemptSeed">
    ///     When supplied, one Queued attempt per eligible run is inserted in the same transaction as the reset, so a
    ///     cohort is never left reset with only part of its set re-judged.
    ///     <see cref="BenchmarkJudgeAttemptSeed.ExpectedJudgePolicyRevisionId" /> is ignored here — the hash already
    ///     names the revision the runtime was resolved for.
    /// </param>
    /// <returns>The active revision, whether this call created it, and the eligible runs (enqueued, given a seed).</returns>
    Task<BenchmarkJudgePolicyActivation> ActivateJudgePolicyAsync(Guid projectId,
        long expectedProjectVersion,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Turns judging off by clearing the project's revision pointer. Revisions and attempts stay as history.
    ///     Refused while any attempt of the project is Queued or Running.
    /// </summary>
    Task DisableJudgePolicyAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the project's quant-fidelity settings, and ONLY those, deliberately ignoring the freeze the ordinary
    ///     project write honours. A frozen project refuses every other edit because its runs were measured against the
    ///     task, context and agent it carries; the fidelity settings are none of those — they decide what gets
    ///     measured NEXT, and every number already stored keeps its own comparability digest.
    ///     <para>
    ///         <paramref name="measureExisting" /> additionally enqueues a fidelity item for every succeeded,
    ///         non-warm-up, first-of-its-repeat-group run that has no fidelity attempt yet, in the same transaction —
    ///         the same rule freeze applies per cell. The count is reported so the operator sees what they started.
    ///     </para>
    /// </summary>
    Task<BenchmarkProjectFidelityChange> UpdateProjectFidelityAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkProjectFidelityInput input,
        bool measureExisting = false,
        CancellationToken cancellationToken = default);

    /// <summary>One judge attempt by id, payloads included, or null when it is gone.</summary>
    Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    Task<BenchmarkFidelityAttemptRecord?> GetFidelityAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>Every fidelity attempt of a run, newest sequence first — the audit trail behind the projection.</summary>
    Task<IReadOnlyList<BenchmarkFidelityAttemptRecord>> ListFidelityAttemptsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The base-logit digests a queued or running fidelity attempt is about to read. Cache eviction must not
    ///     delete a file a measurement is on its way to using.
    /// </summary>
    Task<IReadOnlySet<string>> ListLiveFidelityDigestsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether any fidelity work item is queued or running — the guard on clearing the base cache.</summary>
    Task<bool> HasLiveFidelityWorkAsync(CancellationToken cancellationToken = default);

    /// <summary>One judge policy revision by id, payload included, or null when it is gone.</summary>
    Task<BenchmarkJudgePolicyRevisionRecord?> GetJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>The revision the project judges under, payload included, or null when judging is off.</summary>
    Task<BenchmarkJudgePolicyRevisionRecord?> GetCurrentJudgePolicyRevisionAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every revision the project has ever activated, oldest first. The encrypted policy payload is deliberately
    ///     NOT read: listing revisions must not decrypt one blob per row.
    /// </summary>
    Task<IReadOnlyList<BenchmarkJudgePolicyRevisionRecord>> ListJudgePolicyRevisionsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Queues one more judging of a succeeded run under the project's current policy revision, as a new attempt
    ///     plus its work item, in one transaction.
    /// </summary>
    /// <exception cref="BenchmarkJudgePolicyChangedException">
    ///     The revision named by the command is no longer the project's current one; the caller re-resolves and retries.
    /// </exception>
    Task<BenchmarkJudgeAttemptRecord> EnqueueJudgeAttemptAsync(BenchmarkEnqueueJudgeAttemptCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Claims the rank cohort for <paramref name="executionKey" />: an insert-if-null compare-and-swap that only
    ///     succeeds while the revision still carries <paramref name="cohortGeneration" /> and no key yet. Called from
    ///     inside the transaction that stores a successful attempt's result, never at launch readiness — a failed
    ///     first attempt must not poison the cohort.
    /// </summary>
    /// <returns><see langword="true" /> when this call promoted the key.</returns>
    Task<bool> TryPromoteReferenceExecutionKeyAsync(Guid revisionId,
        int cohortGeneration,
        string executionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens a project-wide re-judge: refuses while any attempt of the project is active, resets the current
    ///     revision's cohort (reference key cleared, generation bumped), bumps the project version, and returns the
    ///     succeeded runs. The reset and the eligible set are decided in one transaction, so a re-judge is never
    ///     partial.
    /// </summary>
    /// <param name="cohortAttemptSeed">
    ///     When supplied, one Queued attempt per eligible run is inserted in the same transaction as the reset.
    ///     <see cref="BenchmarkJudgeAttemptSeed.ExpectedJudgePolicyRevisionId" /> is honoured: a project that moved to
    ///     another revision since the runtime was resolved throws <see cref="BenchmarkJudgePolicyChangedException" />.
    /// </param>
    Task<BenchmarkJudgePolicyActivation> BeginProjectRejudgeAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default);
}
