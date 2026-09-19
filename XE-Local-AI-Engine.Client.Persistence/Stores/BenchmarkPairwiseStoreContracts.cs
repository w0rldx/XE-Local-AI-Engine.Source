namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One run that may be paired against another. <see cref="TaskCaseId" /> and <see cref="TaskInputHash" />
///     are the identity of WHAT WAS ASKED: pairs form only inside one of them, because "which answer is better" is
///     meaningless when the two answers are to different questions. In the current schema a project is one case, so
///     both use the constant below and the grouping is a no-op. The columns retain the identity needed by a schema
///     that supports multiple cases per project.
/// </summary>
public sealed class BenchmarkPairwiseCandidate
{
    public required Guid RunId { get; init; }

    public required Guid? TaskCaseId { get; init; }

    public required string TaskInputHash { get; init; }
}

/// <summary>One unordered pair to compare, canonical (<see cref="RunAId" /> &lt; <see cref="RunBId" />).</summary>
public sealed class BenchmarkPairwiseSlot
{
    public required Guid RunAId { get; init; }

    public required Guid RunBId { get; init; }

    public required Guid? TaskCaseId { get; init; }

    public required string TaskInputHash { get; init; }
}

/// <summary>
///     One pairwise judging, as the planner, the fitter and the verdict-matrix read see it. The encrypted rationale is
///     read only by the detail path; a list never decrypts one.
/// </summary>
public sealed class BenchmarkComparisonRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid PolicyRevisionId { get; init; }

    public required int CohortGeneration { get; init; }

    public required Guid? TaskCaseId { get; init; }

    public required string TaskInputHash { get; init; }

    public required Guid RunAId { get; init; }

    public required Guid RunBId { get; init; }

    public required int Order { get; init; }

    public required int AttemptSequence { get; init; }

    public required int Sequence { get; init; }

    public required BenchmarkJudgeAttemptStatus Status { get; init; }

    public required string? Verdict { get; init; }

    public required bool AnswerATruncated { get; init; }

    public required bool AnswerBTruncated { get; init; }

    public required string? JudgeExecutionKey { get; init; }

    public required string? ErrorMessage { get; init; }

    public required ReadOnlyMemory<byte>? JudgeRuntimeJson { get; init; }

    public required long EnqueuedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long Version { get; init; }

    public BenchmarkRunLaunchIntent? LaunchIntent { get; init; }
}

/// <summary>The whole pairwise picture of one project at one consistent moment.</summary>
public sealed class BenchmarkPairwiseCohortState
{
    /// <summary>Null when judging is off — there is no cohort to pair inside.</summary>
    public required Guid? PolicyRevisionId { get; init; }

    public required int CohortGeneration { get; init; }

    public required int ComparisonSetVersion { get; init; }

    public required string? ReferenceExecutionKey { get; init; }

    public required long ProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkPairwiseCandidate> Candidates { get; init; }

    public required IReadOnlyList<BenchmarkComparisonRecord> Comparisons { get; init; }
}

public sealed class BenchmarkComparisonSuccessCommand
{
    public required long QueueSequence { get; init; }

    public required long ExpectedWorkVersion { get; init; }

    /// <summary><c>a</c>, <c>b</c> or <c>tie</c>, already normalized back to the canonical pair.</summary>
    public required string Verdict { get; init; }

    public required ReadOnlyMemory<byte>? ResultJson { get; init; }

    public required bool AnswerATruncated { get; init; }

    public required bool AnswerBTruncated { get; init; }
}

/// <inheritdoc cref="IBenchmarkStore.PublishPairwiseFitAsync" />
public sealed class BenchmarkPairwiseFitCommand
{
    public required Guid ProjectId { get; init; }

    public required Guid PolicyRevisionId { get; init; }

    public required int CohortGeneration { get; init; }

    public required Guid? TaskCaseId { get; init; }

    public required string FitKey { get; init; }

    public required string JudgeExecutionKey { get; init; }

    public required int ComparisonSetVersion { get; init; }

    public required string FittedSetJson { get; init; }

    public required string ScoresJson { get; init; }

    public required int Iterations { get; init; }

    public required int BootstrapReplicates { get; init; }
}

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
public sealed class BenchmarkPairwiseFitRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid PolicyRevisionId { get; init; }

    public required int CohortGeneration { get; init; }

    public required Guid? TaskCaseId { get; init; }

    public required string FitKey { get; init; }

    public required string JudgeExecutionKey { get; init; }

    public required int ComparisonSetVersion { get; init; }

    public required string FittedSetJson { get; init; }

    public required string ScoresJson { get; init; }

    public required int Iterations { get; init; }

    public required int BootstrapReplicates { get; init; }

    public required long CreatedAtUtc { get; init; }
}
