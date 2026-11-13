namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Pairwise comparisons, their cohort and the published Bradley-Terry fits. One role of <c>BenchmarkStore</c>,
///     registered as the same scoped instance as its siblings.
/// </summary>
public interface IBenchmarkPairwiseStore
{
    /// <summary>
    ///     Terminalizes a comparison work item and its comparison as failed. Keyed by queue sequence rather than by
    ///     run, because a comparison names TWO runs and "the run's comparison work item" is not a well-formed lookup.
    /// </summary>
    Task MarkComparisonFailedAsync(long queueSequence, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MarkComparisonFailedAsync" />
    Task MarkComparisonCancelledAsync(long queueSequence, long expectedWorkVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Everything the pairwise planner, the fitter and the pre-flight estimate read: the project's current cohort
    ///     scope, the runs eligible to be paired inside it, and the comparisons that already exist.
    /// </summary>
    /// <remarks>One read, because all three questions are about the same consistent moment.</remarks>
    Task<BenchmarkPairwiseCohortState> GetPairwiseCohortAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts BOTH presentation orders of every slot that has no live-or-succeeded comparison yet, plus their work
    ///     items, in ONE transaction, and bumps the revision's <c>ComparisonSetVersion</c> in the same one.
    /// </summary>
    /// <remarks>
    ///     Idempotent: a slot a concurrent caller already created violates the filtered unique index, and the violation
    ///     is swallowed — a half-created cohort never completes and therefore never publishes a score.
    /// </remarks>
    /// <returns>How many comparison rows this call created.</returns>
    Task<int> EnsureComparisonsAsync(Guid projectId,
        IReadOnlyList<BenchmarkPairwiseSlot> slots,
        ReadOnlyMemory<byte>? judgeRuntimeJson,
        BenchmarkRunLaunchIntent? launchIntent,
        CancellationToken cancellationToken = default);

    /// <summary>One comparison by id, payloads included, or null when it is gone.</summary>
    Task<BenchmarkComparisonRecord?> GetComparisonAsync(Guid comparisonId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IBenchmarkJudgeStore.MarkJudgeLaunchReadyAsync" />
    Task<bool> MarkComparisonLaunchReadyAsync(Guid comparisonId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records one comparison's verdict and terminalizes its work item, bumping the cohort's
    ///     <c>ComparisonSetVersion</c> in the same transaction.
    /// </summary>
    /// <remarks>
    ///     Inserting and terminalizing are the only two ways the fitted set can change, so they are the only two places
    ///     that bump it.
    /// </remarks>
    Task MarkComparisonSucceededAsync(BenchmarkComparisonSuccessCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a fit as ONE pointer switch: deactivate the scope's active row and insert the new one, in one
    ///     transaction.
    /// </summary>
    /// <remarks>
    ///     A duplicate publication violates <c>ux_benchmark_pairwise_fits_key</c> and no-ops, and the filtered active
    ///     index makes two active fits in one scope unrepresentable rather than merely unlikely.
    /// </remarks>
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
}
