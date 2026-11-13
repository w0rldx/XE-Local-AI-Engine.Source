namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Judge policy revisions, judge attempts and the artifacts a judging produced. One role of <c>BenchmarkStore</c>,
///     registered as the same scoped instance as its siblings.
/// </summary>
public interface IBenchmarkJudgeStore
{
    Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        MarkJudgeCancelledAsync(runId, expectedRunVersion, cancellationToken);

    /// <inheritdoc cref="IBenchmarkRunStore.MarkPrimaryLaunchReadyAsync" />
    /// <param name="attemptId">The judge attempt this launch belongs to — judge evidence lives on the attempt, not the run.</param>
    /// <param name="judgeExecutionKey">
    ///     The rank-cohort key computed from this launch's effective evidence, or <see langword="null" /> when the
    ///     execution identity was incomplete (fail-closed: it is never ranked).
    /// </param>
    Task<bool> MarkJudgeLaunchReadyAsync(Guid attemptId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Points the project at the judge policy with this hash, creating the revision when the project has never
    ///     seen it, and resets that revision's rank cohort (reference key cleared, generation bumped).
    /// </summary>
    /// <remarks>
    ///     The reset means only attempts enqueued after this call can define the cohort. Activating the hash the
    ///     project is already on is a no-op that resets nothing, and the call is refused while any attempt of the
    ///     project is Queued or Running, so a reset always covers the complete eligible set — with a seed, the attempts
    ///     go in the same transaction, so a cohort is never left reset with only part of its set re-judged.
    /// </remarks>
    /// <param name="cohortAttemptSeed">
    ///     When supplied, one Queued attempt per eligible run is inserted in the same transaction as the reset.
    ///     <see cref="BenchmarkJudgeAttemptSeed.ExpectedJudgePolicyRevisionId" /> is ignored: the hash names it.
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

    /// <summary>One judge attempt by id, payloads included, or null when it is gone.</summary>
    Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

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
    ///     succeeds while the revision still carries <paramref name="cohortGeneration" /> and no key yet.
    /// </summary>
    /// <remarks>
    ///     Called from inside the transaction that stores a successful attempt's result, never at launch readiness — a
    ///     failed first attempt must not poison the cohort.
    /// </remarks>
    /// <returns><see langword="true" /> when this call promoted the key.</returns>
    Task<bool> TryPromoteReferenceExecutionKeyAsync(Guid revisionId,
        int cohortGeneration,
        string executionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens a project-wide re-judge: refuses while any attempt of the project is active, resets the current
    ///     revision's cohort (reference key cleared, generation bumped), bumps the project version, returns the runs.
    /// </summary>
    /// <remarks>
    ///     The reset and the eligible set are decided in one transaction, so a re-judge is never partial. A project
    ///     that moved revision since the runtime was resolved throws
    ///     <see cref="BenchmarkJudgePolicyChangedException" />.
    /// </remarks>
    /// <param name="cohortAttemptSeed">
    ///     When supplied, one Queued attempt per eligible run is inserted in the same transaction as the reset, and
    ///     <see cref="BenchmarkJudgeAttemptSeed.ExpectedJudgePolicyRevisionId" /> is honored.
    /// </param>
    Task<BenchmarkJudgePolicyActivation> BeginProjectRejudgeAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the metadata of artifacts one judging produced, whose blobs are already written, in one transaction.
    ///     A second artifact of the same kind for the same criterion of the same attempt is refused.
    /// </summary>
    Task<IReadOnlyList<BenchmarkRunArtifactRecord>> AddRunArtifactsAsync(Guid runId,
        Guid judgeAttemptId,
        IReadOnlyList<BenchmarkRunArtifactCommand> artifacts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRunArtifactRecord>> GetRunArtifactsAsync(Guid runId, CancellationToken cancellationToken = default);
}
