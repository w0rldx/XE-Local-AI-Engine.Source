namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One run that may be paired against another. <paramref name="TaskCaseId" /> and <paramref name="TaskInputHash" />
///     are the identity of WHAT WAS ASKED: pairs form only inside one of them, because "which answer is better" is
///     meaningless when the two answers are to different questions. In the current schema a project is one case, so
///     both use the constant below and the grouping is a no-op. The columns retain the identity needed by a schema
///     that supports multiple cases per project.
/// </summary>
public sealed record BenchmarkPairwiseCandidate(Guid RunId, Guid? TaskCaseId, string TaskInputHash);

/// <summary>One unordered pair to compare, canonical (<paramref name="RunAId" /> &lt; <paramref name="RunBId" />).</summary>
public sealed record BenchmarkPairwiseSlot(Guid RunAId, Guid RunBId, Guid? TaskCaseId, string TaskInputHash);

/// <summary>
///     One pairwise judging, as the planner, the fitter and the verdict-matrix read see it. The encrypted rationale is
///     read only by the detail path; a list never decrypts one.
/// </summary>
public sealed record BenchmarkComparisonRecord(
    Guid Id,
    Guid ProjectId,
    Guid PolicyRevisionId,
    int CohortGeneration,
    Guid? TaskCaseId,
    string TaskInputHash,
    Guid RunAId,
    Guid RunBId,
    int Order,
    int AttemptSequence,
    int Sequence,
    BenchmarkJudgeAttemptStatus Status,
    string? Verdict,
    bool AnswerATruncated,
    bool AnswerBTruncated,
    string? JudgeExecutionKey,
    string? ErrorMessage,
    ReadOnlyMemory<byte>? JudgeRuntimeJson,
    long EnqueuedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Version,
    BenchmarkRunLaunchIntent? LaunchIntent = null);

/// <summary>The whole pairwise picture of one project at one consistent moment.</summary>
/// <param name="PolicyRevisionId">Null when judging is off — there is no cohort to pair inside.</param>
public sealed record BenchmarkPairwiseCohortState(
    Guid? PolicyRevisionId,
    int CohortGeneration,
    int ComparisonSetVersion,
    string? ReferenceExecutionKey,
    long ProjectVersion,
    IReadOnlyList<BenchmarkPairwiseCandidate> Candidates,
    IReadOnlyList<BenchmarkComparisonRecord> Comparisons);

/// <param name="Verdict"><c>a</c>, <c>b</c> or <c>tie</c>, already normalized back to the canonical pair.</param>
public sealed record BenchmarkComparisonSuccessCommand(
    long QueueSequence,
    long ExpectedWorkVersion,
    string Verdict,
    ReadOnlyMemory<byte>? ResultJson,
    bool AnswerATruncated,
    bool AnswerBTruncated);

/// <inheritdoc cref="IBenchmarkStore.PublishPairwiseFitAsync" />
public sealed record BenchmarkPairwiseFitCommand(
    Guid ProjectId,
    Guid PolicyRevisionId,
    int CohortGeneration,
    Guid? TaskCaseId,
    string FitKey,
    string JudgeExecutionKey,
    int ComparisonSetVersion,
    string FittedSetJson,
    string ScoresJson,
    int Iterations,
    int BootstrapReplicates);

/// <summary>
///     One run's row inside a fit's <c>ScoresJson</c> — one entry per ELIGIBLE run, not per fitted one, because a run
///     the cap left out or the comparison graph stranded must be able to say why it has no score from this row alone.
/// </summary>
/// <param name="Reason">
///     Null when <paramref name="Score" /> ranks. Otherwise the <see cref="BenchmarkRunJudgeStates" /> pairwise reason:
///     a whole-fit refusal puts the same one on every entry, so a refusal reaches the ranking read without it having
///     to open a single comparison row.
/// </param>
public sealed record BenchmarkPairwiseScoreEntry(
    Guid RunId,
    int? Score,
    int? CiLow,
    int? CiHigh,
    int Comparisons,
    int BootstrapAppearances,
    string? Reason);

/// <summary>One published fit. <see cref="ScoresJson" /> is the rank input, so it is plaintext and read once per page.</summary>
public sealed record BenchmarkPairwiseFitRecord(
    Guid Id,
    Guid ProjectId,
    Guid PolicyRevisionId,
    int CohortGeneration,
    Guid? TaskCaseId,
    string FitKey,
    string JudgeExecutionKey,
    int ComparisonSetVersion,
    string FittedSetJson,
    string ScoresJson,
    int Iterations,
    int BootstrapReplicates,
    long CreatedAtUtc);
