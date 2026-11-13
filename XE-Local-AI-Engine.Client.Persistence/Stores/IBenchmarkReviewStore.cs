namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Reviewer decisions and the review inbox. One role of <c>BenchmarkStore</c>, registered as the same scoped instance as its siblings.</summary>
public interface IBenchmarkReviewStore
{
    /// <summary>
    ///     Records a reviewer's decision on one run in one transaction: upserts the review row, writes the approved
    ///     score onto the run's user score (a rejection clears it) and bumps the run version.
    /// </summary>
    /// <remarks>
    ///     Refused with <c>NotReviewable</c> unless the run succeeded, is not a warm-up, belongs to a project that
    ///     requires review and answered a scoring item (a leaf that counts toward the score, or no item at all);
    ///     <c>VersionConflict</c> on a stale <see cref="BenchmarkReviewCommand.ExpectedRunVersion" />. The returned
    ///     record carries the review.
    /// </remarks>
    Task<BenchmarkRunRecord> SubmitReviewAsync(BenchmarkReviewCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of reviewable runs in <see cref="BenchmarkReviewQuery.State" />, from flat columns only. Pending is
    ///     derived: no decision, or a decision under a superseded policy revision.
    /// </summary>
    Task<BenchmarkReviewQueuePage> ListReviewQueueAsync(BenchmarkReviewQuery query, CancellationToken cancellationToken = default);
}
