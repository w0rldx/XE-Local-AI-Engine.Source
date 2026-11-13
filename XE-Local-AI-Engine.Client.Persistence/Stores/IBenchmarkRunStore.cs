namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Benchmark runs, their cells and the primary phase's transitions. One role of <c>BenchmarkStore</c>, registered as
///     the same scoped instance as its siblings.
/// </summary>
public interface IBenchmarkRunStore
{
    /// <summary>
    ///     Starts ONE run. Shorthand for a single-item <see cref="StartRunsAsync" /> against the command's own
    ///     <see cref="BenchmarkStartRunCommand.ExpectedProjectVersion" />.
    /// </summary>
    Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a whole group of runs ATOMICALLY: one transaction, one compare-and-swap against
    ///     <paramref name="expectedProjectVersion" />, one <c>project.Version += commands.Count</c>, and the runs plus
    ///     their primary work items inserted in the given (FIFO execution) order.
    /// </summary>
    /// <remarks>
    ///     All-or-nothing is the point: one run per transaction, each chaining its CAS on its predecessor, lets a
    ///     concurrent writer land mid-group, so the caller gets a conflict and no ids while the runs already inserted
    ///     stay queued and consume the exclusive runtime, and a batch caller cannot chain because the project version
    ///     moved by a number it was never told. Every command must name the same project, and a
    ///     <see cref="BenchmarkStartRunCommand.FreezeCommitGuard" /> is evaluated once per distinct guard instance.
    /// </remarks>
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
    ///     Every run of a project in one call, ranked once.
    /// </summary>
    /// <remarks>
    ///     The paging overload recomputes the whole-project ranking — a full scan plus a judge-view join across three
    ///     more tables — for each page, and an export reads every page. The default body pages through
    ///     <see cref="ListRunsAsync" /> so a test double needs no extra member.
    /// </remarks>
    Task<BenchmarkRunPage> ListAllRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        ListRunsAsync(projectId, skip: 0, int.MaxValue, modelContentFingerprint: null, includeUnscored: true, cancellationToken);

    /// <summary>
    ///     The project's measurement CELLS: one model, one KV type, one repeat of the whole task-item suite.
    /// </summary>
    /// <remarks>
    ///     A cell is what ranks, so this is the shape a comparison reads — the per-run listing shows the same numbers
    ///     one row at a time and cannot say which items a cell is missing.
    /// </remarks>
    Task<BenchmarkCellPage> ListCellsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>How many runs a project has, counted in the database.</summary>
    Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run count of EVERY project that has one, in a single grouped count — what the project listing needs, so
    ///     it does not ask <see cref="CountRunsAsync" /> once per row. A project with no runs is absent from the
    ///     dictionary rather than present with a zero.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountRunsByProjectAsync(CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    /// <param name="primaryStopReason">
    ///     Why generation stopped, when the failure says so — <c>timeout</c> for a run cancelled at its invocation
    ///     budget. Null leaves the column untouched rather than guessing.
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

    /// <summary>
    ///     Records the primary phase's durable launch evidence: an insert-if-null write of the receipt and environment
    ///     columns, keyed by the immutable work item rather than by the run's mutable version.
    /// </summary>
    /// <remarks>
    ///     It never overwrites an existing block and never changes any status, so it is safe to call before inference
    ///     and again while terminalizing. Terminalizing a work item bumps its version by exactly one, which is the
    ///     cancel-first ordering <paramref name="claimedWorkVersion" /> accepts.
    /// </remarks>
    /// <returns><see langword="true" /> when this call wrote the block.</returns>
    /// <param name="workItemId">The claimed work item's queue sequence.</param>
    /// <param name="claimedWorkVersion">
    ///     The version the caller claimed: accepted while the work item is still <c>Running</c> at it, or already
    ///     <c>Cancelled</c> at its successor. Anything else is refused.
    /// </param>
    Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets or clears the operator's 0..100 override. <see langword="null" /> clears it; the judge score stays
    ///     visible beside it either way.
    /// </summary>
    /// <remarks>
    ///     Refused with <c>ReviewRequired</c> in a project that requires review: there the review is the only writer of
    ///     the user score (<see cref="IBenchmarkReviewStore.SubmitReviewAsync" />).
    /// </remarks>
    Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int? score, long expectedRunVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes a terminal run and everything that pointed at it: work items, judge attempts, fidelity attempts,
    ///     and every pairwise comparison it took part in on EITHER side.
    /// </summary>
    /// <remarks>
    ///     Refused while any of those is Queued or Running, including a comparison whose canonical first run is the
    ///     other one, which no work item names. Deleting comparisons bumps the affected revisions'
    ///     <c>ComparisonSetVersion</c> and deactivates the project's active pairwise fits, because a fit whose fitted
    ///     set names a deleted run ranks something that is not there. The next planner pass re-fits what is left.
    /// </remarks>
    Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
}
