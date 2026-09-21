namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     What the run executor resolved for the automatic first judging, carried into the same transaction that commits
///     primary success so a crash can never leave a succeeded run without its attempt.
/// </summary>
public sealed record BenchmarkJudgeAttemptSeed
{
    /// <summary>
    ///     The revision <see cref="RuntimeJson" /> was resolved for. When the project has moved on the store rolls back and
    ///     throws <see cref="BenchmarkJudgePolicyChangedException" />, so the caller can re-resolve and retry.
    /// </summary>
    public Guid? ExpectedJudgePolicyRevisionId { get; init; }

    /// <summary>
    ///     The judge's frozen launch configuration. <see langword="null" /> means resolution failed, and the attempt is
    ///     inserted directly as Failed together with a terminal work item.
    /// </summary>
    public ReadOnlyMemory<byte>? RuntimeJson { get; init; }

    public string? RuntimeUnresolvedReason { get; init; }

    public BenchmarkRunLaunchIntent? LaunchIntent { get; init; }

    /// <summary>
    ///     Whether a cohort-wide reset inserts one POINTWISE attempt per eligible run.
    /// </summary>
    /// <remarks>
    ///     False for a pairwise policy, whose cohort is judged by comparisons the planner enqueues instead: a pairwise
    ///     cohort carrying pointwise attempts judges every run a second way and ranks off whichever source answered.
    ///     The seed is still supplied, because <see cref="ExpectedJudgePolicyRevisionId" /> pins the revision the
    ///     caller resolved against.
    /// </remarks>
    public bool SeedPointwiseAttempts { get; init; } = true;
}

/// <inheritdoc cref="IBenchmarkStore.EnqueueJudgeAttemptAsync" />
public sealed record BenchmarkEnqueueJudgeAttemptCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedRunVersion { get; init; }

    public required Guid PolicyRevisionId { get; init; }

    public ReadOnlyMemory<byte>? RuntimeJson { get; init; }

    public string? RuntimeUnresolvedReason { get; init; }

    /// <summary>Bypasses the already-applied guard for a deliberate operator re-judge.</summary>
    public bool Force { get; init; }

    public BenchmarkRunLaunchIntent? LaunchIntent { get; init; }
}

public sealed class BenchmarkJudgePolicyRevisionRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required int Revision { get; init; }

    /// <summary>Null on a listing, which never decrypts the payload.</summary>
    public required ReadOnlyMemory<byte>? PolicyJson { get; init; }

    public required string PolicyHash { get; init; }

    public required string? ReferenceExecutionKey { get; init; }

    public required int CohortGeneration { get; init; }

    public required long CreatedAtUtc { get; init; }

    public int ComparisonSetVersion { get; init; }
}

public sealed class BenchmarkJudgePolicyActivation
{
    public required BenchmarkJudgePolicyRevisionRecord Revision { get; init; }

    public required bool WasCreated { get; init; }

    /// <summary>
    ///     The project's succeeded runs with stored output — the complete eligible set. With a cohort attempt seed these
    ///     are exactly the runs an attempt was enqueued for, in enqueue order. Empty on a no-op activation.
    /// </summary>
    public required IReadOnlyList<Guid> SucceededRunIds { get; init; }
}

public sealed class BenchmarkJudgeAttemptRecord
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required int Sequence { get; init; }

    public required Guid PolicyRevisionId { get; init; }

    public required int CohortGeneration { get; init; }

    public required ReadOnlyMemory<byte>? JudgeRuntimeJson { get; init; }

    public required string? JudgeExecutionKey { get; init; }

    public required BenchmarkJudgeAttemptStatus Status { get; init; }

    public required ReadOnlyMemory<byte>? ResultJson { get; init; }

    public required int? Score { get; init; }

    public required string? ErrorMessage { get; init; }

    public required long EnqueuedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long Version { get; init; }

    public BenchmarkRunLaunchIntent? LaunchIntent { get; init; }

    public BenchmarkRunLaunchEvidence? LaunchEvidence { get; init; }
}

public sealed record BenchmarkJudgeSuccessCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedWorkVersion { get; init; }

    public required ReadOnlyMemory<byte> JudgeResultJson { get; init; }

    public long LastStreamSequence { get; init; }

    /// <summary>The server-computed 0..100 rubric score stored on the attempt, plaintext and sortable.</summary>
    public int? Score { get; init; }

    /// <summary>
    ///     The cohort key for a judging that ran no model because every rubric criterion was verified server-side.
    ///     Applied only when the attempt has no measured execution key.
    /// </summary>
    public string? VerifiedExecutionKey { get; init; }
}

/// <summary>
///     The run-level judge state derived from the run's current attempt and the project's current policy revision.
/// </summary>
public sealed record BenchmarkRunJudgeView
{
    /// <summary><c>none</c> when there is no attempt, otherwise the current attempt's status, lowercased.</summary>
    public required string State { get; init; }

    public required Guid? AttemptId { get; init; }

    public required int? Score { get; init; }

    public required int? PolicyRevision { get; init; }

    public required Guid? PolicyRevisionId { get; init; }

    public required int? AttemptSequence { get; init; }

    public required int? CohortGeneration { get; init; }

    public required string? ExecutionKey { get; init; }

    public required string? ErrorMessage { get; init; }

    public required bool PolicyCurrent { get; init; }

    public required bool ExecutionCurrent { get; init; }

    /// <summary>Why this run is not in the ranked cohort, or <see langword="null" /> when it is ranked.</summary>
    public required string? RankExclusionReason { get; init; }
}

/// <summary>
///     The <see cref="BenchmarkRunRecord.PrimaryStopReason" /> vocabulary. Values are the provider's own
///     <c>ChatFinishReason</c> tokens, stored verbatim — this class names only the ones the node reasons about, and an
///     unrecognized token is stored and displayed rather than rejected.
/// </summary>
public static class BenchmarkPrimaryStopReasons
{
    /// <summary>Generation ran out of budget: <c>n_predict</c> exhausted, or the context window filled.</summary>
    public const string Length = "length";

    /// <summary>The node cancelled the run at its invocation timeout before the model stopped on its own.</summary>
    public const string Timeout = "timeout";

    /// <summary>
    ///     Generation ran out of budget while still inside its reasoning: <see cref="Length" />, and not one visible
    ///     answer token was emitted.
    /// </summary>
    /// <remarks>
    ///     Truncated for every consumer (<see cref="IsTruncated" /> covers it), but it names the reasoning budget as
    ///     the thing to raise rather than the output budget, which is the whole difference between a run an operator
    ///     can fix and one they cannot explain.
    /// </remarks>
    public const string ReasoningLength = "reasoning-length";

    /// <summary>
    ///     The invocation ended cleanly but produced no answer: the turn stopped on an unanswered tool call, or every
    ///     token it emitted was reasoning.
    /// </summary>
    /// <remarks>
    ///     Node-derived, not a provider token — llama-server reports <c>stop</c> or <c>tool_calls</c> for both shapes,
    ///     which read as a finished answer everywhere downstream and would let a run that answered NOTHING be judged
    ///     and ranked against runs that did.
    /// </remarks>
    public const string Incomplete = "incomplete";

    /// <summary>
    ///     Whether the primary generation stopped because it ran out of budget.
    /// </summary>
    /// <remarks>
    ///     <see cref="Length" /> is the OpenAI-compatible token for BOTH causes llama-server reports it for —
    ///     <c>n_predict</c> exhausted and the context window full (<c>stopped_limit</c>) — and both mean the answer is
    ///     cut off; <see cref="ReasoningLength" /> narrows the same fact and is therefore also truncated. ONE
    ///     implementation on purpose: ranking (<c>BenchmarkStore.ApplyRunExclusions</c>) and judging
    ///     (<c>BenchmarkJudgeExecutor</c>) are in different assemblies, and a token added to only one would make ranking exclude a run the judge was never told was cut off.
    /// </remarks>
    public static bool IsTruncated(string? primaryStopReason) =>
        string.Equals(primaryStopReason, Length, StringComparison.OrdinalIgnoreCase)
        || string.Equals(primaryStopReason, ReasoningLength, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether the primary generation ended without producing an answer. Same posture as
    ///     <see cref="IsTruncated" />: one implementation, because ranking and judging live in different assemblies and
    ///     must agree on exactly which runs carry no gradable answer.
    /// </summary>
    public static bool IsIncomplete(string? primaryStopReason) =>
        string.Equals(primaryStopReason, Incomplete, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The <see cref="BenchmarkRunJudgeView.State" /> and <see cref="BenchmarkRunJudgeView.RankExclusionReason" /> vocabularies.</summary>
public static class BenchmarkRunJudgeStates
{
    public const string None = "none";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public const string ReasonNoScore = "no-score";
    public const string ReasonJudgePending = "judge-pending";
    public const string ReasonJudgeFailed = "judge-failed";

    /// <summary>An operator-cancelled judging is not a failed one: re-judging is all it needs.</summary>
    public const string ReasonJudgeCancelled = "judge-cancelled";

    public const string ReasonPolicyOutdated = "policy-outdated";
    public const string ReasonGenerationStale = "generation-stale";
    public const string ReasonExecutionKeyMismatch = "execution-key-mismatch";
    public const string ReasonExecutionIdentityIncomplete = "execution-identity-incomplete";

    /// <summary>
    ///     The primary generation was cut off by the token budget or the context ceiling (<c>finish_reason=length</c>).
    /// </summary>
    /// <remarks>
    ///     The measurement is still a real one — the run stays <c>Succeeded</c> — but an incomplete answer must not be
    ///     ranked against complete ones, whatever the judge scored it. An operator score still overrides.
    /// </remarks>
    public const string ReasonTruncated = "truncated";

    /// <summary>
    ///     The primary generation finished cleanly but produced no answer at all — it stopped on an unanswered tool
    ///     call, or emitted only reasoning.
    /// </summary>
    /// <remarks>
    ///     Excluded for the same reason as <see cref="ReasonTruncated" /> and with the same operator override: there
    ///     is nothing for a rubric to grade, so whatever a judge scored it cannot rank against runs that answered.
    /// </remarks>
    public const string ReasonIncomplete = "incomplete";

    /// <summary>
    ///     A warm-up run: a real measurement, kept and shown, but exactly the first-launch cost the repeats after it
    ///     were meant NOT to pay.
    /// </summary>
    /// <remarks>
    ///     Ranking it against them would rank the thing being controlled for. Unlike every other reason here, an
    ///     operator score does not override it: a warm-up is not a contender.
    /// </remarks>
    public const string ReasonWarmup = "warmup";

    /// <summary>Pairwise mode, comparisons of this cohort still outstanding. Waiting is all it needs.</summary>
    public const string ReasonPairwisePending = "pairwise-pending";

    /// <summary>
    ///     Pairwise mode, and this run carries no fitted strength: fewer than two verdicts, a minority component of a
    ///     disconnected comparison graph, or a cohort too truncated to aggregate. More runs, or more comparisons.
    /// </summary>
    public const string ReasonPairwiseInsufficient = "pairwise-insufficient";

    /// <summary>
    ///     Pairwise mode, and this run is past the per-cohort cap, so nothing was ever compared against it. Removing
    ///     runs is the fix — a sampled subset of the tournament would be a silently biased one.
    /// </summary>
    public const string ReasonPairwiseCap = "pairwise-cap";

    /// <summary>The active fit was fit over a set the cohort has since changed. Re-fitting is automatic; this is transient.</summary>
    public const string ReasonPairwiseStale = "pairwise-stale";

    /// <summary>The Bradley–Terry sweep did not converge, so no strength was published rather than a half-fit one.</summary>
    public const string ReasonPairwiseUnfitted = "pairwise-unfitted";

    /// <summary>Two answers to different task cases are not comparable and are excluded from pairwise ranking.</summary>
    public const string ReasonPairwiseCrossCase = "pairwise-cross-case";

    /// <summary>
    ///     A comparison in the fitted set was judged by a runtime other than the one the cohort was claimed with.
    /// </summary>
    /// <remarks>
    ///     The whole fit is refused rather than fitted over the matching subset: dropping comparisons changes the
    ///     graph and can disconnect it, publishing a number over a set the operator never chose. Re-judging heals it.
    /// </remarks>
    public const string ReasonPairwiseExecutionMismatch = "pairwise-execution-mismatch";

    /// <summary>Nothing has promoted a reference execution key yet, so no fit can be shown to belong to a cohort.</summary>
    public const string ReasonPairwiseExecutionIdentityIncomplete = "pairwise-execution-identity-incomplete";

    public const string ReasonVerifierUnavailable = "verifier-unavailable";
    public const string VerifierUnavailablePrefix = "verifier-unavailable: ";
    public const string ReasonOverrideUnmatched = "override-unmatched";
    public const string OverrideUnmatchedPrefix = "override-unmatched: ";
    public const string ReasonItemRevised = "item-revised";
    public const string ReasonItemSetRevised = "item-set-revised";
    public const string ReasonItemIncomplete = "item-incomplete";
}
