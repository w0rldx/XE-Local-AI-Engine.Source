namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IBenchmarkStore
{
    /// <summary>
    ///     Creates the project and, with a <paramref name="judgePolicy" />, activates its judge in the SAME
    ///     transaction: a project that persisted with judging off because a second transaction never ran is one an
    ///     operator can only retry into a duplicate.
    /// </summary>
    Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicy = null,
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

    /// <summary>How many runs a project has, counted in the database.</summary>
    Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default);

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

/// <param name="MaxOutputTokens">
///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
///     context-limited. Must be <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
/// </param>
/// <param name="ReasoningBudgetTokens">
///     The per-request thinking budget frozen into every run's sampling, or <see langword="null" /> to leave the
///     reasoning bounded only by the effort ladder and the window. Must be <c>1 &lt;= ReasoningBudgetTokens &lt;
///     ContextTokens</c>.
/// </param>
public sealed record BenchmarkProjectInput(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    int? MaxOutputTokens = null,
    int? InvocationTimeoutSeconds = null,
    int? ReasoningBudgetTokens = null,
    bool FidelityEnabled = false,
    bool FidelityKldEnabled = false,
    int? FidelityChunks = null,
    string? FidelityKldBaseModelName = null,
    string? FidelityKldBaseFingerprint = null);

/// <summary>
///     The judge half of a project write, applied in the project's own transaction. A <see langword="null" /> instance
///     leaves the judge alone; an instance with a <see langword="null" /> <paramref name="PolicyJson" /> disables it.
/// </summary>
public sealed record BenchmarkJudgePolicyChangeInput(ReadOnlyMemory<byte>? PolicyJson, string? PolicyHash)
{
    /// <summary>Turns judging off as part of the project write.</summary>
    public static BenchmarkJudgePolicyChangeInput Disabled { get; } = new(null, null);
}

public sealed record BenchmarkStartRunCommand(
    Guid RunId,
    Guid ProjectId,
    long ExpectedProjectVersion,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    IBenchmarkFreezeCommitGuard? FreezeCommitGuard = null,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    Guid? RepeatGroupId = null,
    int? RepeatIndex = null,
    bool IsWarmup = false,
    int? InvocationTimeoutSeconds = null,
    BenchmarkRepeatMode RepeatMode = BenchmarkRepeatMode.Throughput,
    string? SamplingSeed = null,
    double? SamplingTemperature = null);

/// <summary>
///     Application-owned dependency guard executed by <see cref="IBenchmarkStore.StartRunAsync" /> inside the same
///     transaction that verifies the project version and inserts the run/work rows. Returning <see langword="false" />
///     aborts the transaction with <c>FreezeDependencyChanged</c>.
/// </summary>
public interface IBenchmarkFreezeCommitGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

/// <param name="TokensPerSecond">
///     Decode throughput (tg) when <paramref name="Throughput" /> carries the split, otherwise the blended
///     <c>TotalTokens / DurationMs</c>. Same column, same name, same meaning for every existing reader.
/// </param>
/// <param name="Throughput">
///     The separated throughput measurement, or <see langword="null" /> when the runtime reported none.
/// </param>
public sealed record BenchmarkPrimarySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> OutputPartsJson,
    long LastStreamSequence,
    int EffectiveContextTokens,
    long DurationMs,
    int? TotalTokens,
    double? TokensPerSecond,
    string? PrimaryStopReason = null,
    BenchmarkJudgeAttemptSeed? JudgeAttempt = null,
    BenchmarkRunThroughput? Throughput = null);

/// <summary>
///     One run's separated throughput facts: how long the caller waited for the first token, and how the turn's tokens
///     and milliseconds split between prompt processing (pp) and generation (tg). Persisted as plaintext numerics
///     alongside the blended figures the columns already carried, never instead of them.
///     <para>
///         Display only, by operator decision: no member of this record is a ranking input. <see cref="CachedPromptTokens" />
///         above zero means <see cref="PromptMs" /> measured a partially cached prefill rather than a cold one — it
///         counts tokens served from the prompt cache across ALL of the turn's requests.
///     </para>
/// </summary>
/// <param name="SegmentCount">
///     How many provider requests the turn made, i.e. how many readings the sums are made of. Null on runs recorded
///     before the column existed; 1 for a plain turn; more once the agent called tools, because each tool round is
///     another request that re-sends the conversation and prefills again.
/// </param>
public sealed record BenchmarkRunThroughput(
    double? TtftMs = null,
    int? PromptTokens = null,
    double? PromptMs = null,
    int? GenerationTokens = null,
    double? GenerationMs = null,
    int? CachedPromptTokens = null,
    int? SegmentCount = null)
{
    /// <summary>Prompt-processing throughput (pp) in tokens per second, or null when either input is absent.</summary>
    public double? PromptTokensPerSecond => TokenThroughput.FromMilliseconds(PromptTokens, PromptMs);

    /// <summary>Decode throughput (tg) in tokens per second, or null when either input is absent.</summary>
    public double? GenerationTokensPerSecond => TokenThroughput.FromMilliseconds(GenerationTokens, GenerationMs);
}

/// <summary>
///     What the run executor resolved for the automatic first judging, carried into the same transaction that commits
///     primary success so a crash can never leave a succeeded run without its attempt.
/// </summary>
/// <param name="ExpectedJudgePolicyRevisionId">
///     The revision <see cref="RuntimeJson" /> was resolved for. When the project has moved on the store rolls back and
///     throws <see cref="BenchmarkJudgePolicyChangedException" />, so the caller can re-resolve and retry.
/// </param>
/// <param name="RuntimeJson">
///     The judge's frozen launch configuration. <see langword="null" /> means resolution failed, and the attempt is
///     inserted directly as Failed together with a terminal work item.
/// </param>
/// <param name="SeedPointwiseAttempts">
///     Whether a cohort-wide reset inserts one POINTWISE attempt per eligible run. False for a pairwise policy, whose
///     cohort is judged by comparisons the planner enqueues instead: a pairwise cohort carrying pointwise attempts
///     judges every run a second way and ranks off whichever source answered. The seed is still supplied, because
///     <see cref="ExpectedJudgePolicyRevisionId" /> is what pins the revision the caller resolved against.
/// </param>
public sealed record BenchmarkJudgeAttemptSeed(
    Guid? ExpectedJudgePolicyRevisionId = null,
    ReadOnlyMemory<byte>? RuntimeJson = null,
    string? RuntimeUnresolvedReason = null,
    BenchmarkRunLaunchIntent? LaunchIntent = null,
    bool SeedPointwiseAttempts = true);

/// <inheritdoc cref="IBenchmarkStore.EnqueueJudgeAttemptAsync" />
/// <param name="Force">Bypasses the already-applied guard for a deliberate operator re-judge.</param>
public sealed record BenchmarkEnqueueJudgeAttemptCommand(
    Guid RunId,
    long ExpectedRunVersion,
    Guid PolicyRevisionId,
    ReadOnlyMemory<byte>? RuntimeJson = null,
    string? RuntimeUnresolvedReason = null,
    bool Force = false,
    BenchmarkRunLaunchIntent? LaunchIntent = null);

/// <summary>
///     One run that may be paired against another. <paramref name="TaskCaseId" /> and <paramref name="TaskInputHash" />
///     are the identity of WHAT WAS ASKED: pairs form only inside one of them, because "which answer is better" is
///     meaningless when the two answers are to different questions. In P2 a project is one case, so both are the
///     constant below and the grouping is a no-op — it is the contract P3 widens, and the columns already carry it.
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
    long Version);

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

/// <param name="PolicyJson">Null on a listing, which never decrypts the payload.</param>
public sealed record BenchmarkJudgePolicyRevisionRecord(
    Guid Id,
    Guid ProjectId,
    int Revision,
    ReadOnlyMemory<byte>? PolicyJson,
    string PolicyHash,
    string? ReferenceExecutionKey,
    int CohortGeneration,
    long CreatedAtUtc,
    int ComparisonSetVersion = 0);

/// <param name="SucceededRunIds">
///     The project's succeeded runs with stored output — the complete eligible set. With a cohort attempt seed these
///     are exactly the runs an attempt was enqueued for, in enqueue order. Empty on a no-op activation.
/// </param>
public sealed record BenchmarkJudgePolicyActivation(
    BenchmarkJudgePolicyRevisionRecord Revision,
    bool WasCreated,
    IReadOnlyList<Guid> SucceededRunIds);

public sealed record BenchmarkJudgeAttemptRecord(
    Guid Id,
    Guid RunId,
    int Sequence,
    Guid PolicyRevisionId,
    int CohortGeneration,
    ReadOnlyMemory<byte>? JudgeRuntimeJson,
    string? JudgeExecutionKey,
    BenchmarkJudgeAttemptStatus Status,
    ReadOnlyMemory<byte>? ResultJson,
    int? Score,
    string? ErrorMessage,
    long EnqueuedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Version,
    BenchmarkRunLaunchIntent? LaunchIntent = null,
    BenchmarkRunLaunchEvidence? LaunchEvidence = null);

/// <param name="Score">The server-computed 0..100 rubric score stored on the attempt, plaintext and sortable.</param>
/// <summary>
///     One succeeded fidelity measurement, as it is written down. <paramref name="ReceiptJson" /> is the REDUCED
///     evidence block (executable, argv, corpus, environment facts) — llama-perplexity has no readiness probe, so
///     there is no launch receipt to record, and presenting the reduced block as one would be exactly the drift the
///     display-only axes exist to prevent.
/// </summary>
public sealed record BenchmarkFidelitySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    Guid FidelityAttemptId,
    double? PerplexityMean = null,
    double? PerplexityStdErr = null,
    int? PerplexityChunks = null,
    int? PerplexityContextTokens = null,
    string? CorpusId = null,
    double? KldMean = null,
    double? KldP99 = null,
    double? TopTokenAgreement = null,
    string? BaseModelName = null,
    string? BaseModelContentFingerprint = null,
    string? BaseLogitsDigest = null,
    ReadOnlyMemory<byte> ReceiptJson = default);

/// <param name="VerifiedExecutionKey">
///     The cohort key for a judging that ran no model at all because every rubric criterion was verified
///     server-side. Applied only when the attempt has none — a measured key, written at launch, is never overwritten.
/// </param>
/// <param name="FidelityKldBaseFingerprint">
///     Resolved by the service from the eligible-model catalog, never by a caller: it is an input to the KLD
///     comparability digest, so a supplied value could make numbers measured against different weights compare equal.
/// </param>
public sealed record BenchmarkProjectFidelityInput(
    bool FidelityEnabled,
    bool FidelityKldEnabled,
    int? FidelityChunks,
    string? FidelityKldBaseModelName,
    string? FidelityKldBaseFingerprint);

/// <param name="EnqueuedRunIds">The runs a <c>measureExisting</c> write queued a measurement for; empty otherwise.</param>
public sealed record BenchmarkProjectFidelityChange(BenchmarkProjectRecord Project, IReadOnlyList<Guid> EnqueuedRunIds);

public sealed record BenchmarkJudgeSuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> JudgeResultJson,
    long LastStreamSequence = 0,
    int? Score = null,
    string? VerifiedExecutionKey = null);

/// <param name="JudgeEnabled">Derived: the project judges exactly while it points at a policy revision.</param>
public sealed record BenchmarkProjectRecord(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    Guid? CurrentJudgePolicyRevisionId,
    bool IsFrozen,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    int? MaxOutputTokens = null,
    int? InvocationTimeoutSeconds = null,
    int? ReasoningBudgetTokens = null,
    bool FidelityEnabled = false,
    bool FidelityKldEnabled = false,
    int? FidelityChunks = null,
    string? FidelityKldBaseModelName = null,
    string? FidelityKldBaseFingerprint = null);

/// <param name="Judge">
///     The derived judge view. Everything judge-related is now attempt-owned: a run is judged many times, so nothing
///     about a judging is stored on the run itself beyond the pointer to its current attempt.
/// </param>
public sealed record BenchmarkRunRecord(
    Guid Id,
    Guid ProjectId,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    BenchmarkPrimaryStatus PrimaryStatus,
    int? EffectiveContextTokens,
    long? DurationMs,
    int? TotalTokens,
    double? TokensPerSecond,
    ReadOnlyMemory<byte>? OutputPartsJson,
    long LastStreamSequence,
    int? UserScore,
    string? PrimaryErrorMessage,
    long Version,
    long CreatedAtUtc,
    long? StartedAtUtc,
    long? PrimaryCompletedAtUtc,
    long UpdatedAtUtc,
    BenchmarkRunLaunchIntent? PrimaryLaunchIntent = null,
    BenchmarkRunLaunchEvidence? PrimaryLaunchEvidence = null,
    string? PrimaryStopReason = null,
    BenchmarkRunJudgeView? Judge = null,
    int? QualityScore = null,
    string? QualityScoreSource = null,
    int? Rank = null,
    BenchmarkRunThroughput? Throughput = null,
    Guid? RepeatGroupId = null,
    int? RepeatIndex = null,
    bool IsWarmup = false,
    int? InvocationTimeoutSeconds = null,
    BenchmarkRepeatMode RepeatMode = BenchmarkRepeatMode.Throughput,
    string? SamplingSeed = null,
    double? SamplingTemperature = null,
    BenchmarkRunFidelity? Fidelity = null);

/// <summary>
///     A run's quant-fidelity projection: a copy of the latest succeeded measurement. Display only — perplexity and
///     KL divergence are never ranking inputs, and a KLD figure is shown only while
///     <paramref name="KldBaseLogitsDigest" /> equals the digest the project's current settings recompute.
/// </summary>
public sealed record BenchmarkRunFidelity(
    string? Status,
    Guid? AttemptId,
    double? PerplexityMean,
    double? PerplexityStdErr,
    int? PerplexityChunks,
    int? PerplexityContextTokens,
    string? PerplexityCorpusId,
    double? KldMean,
    double? KldP99,
    double? TopTokenAgreement,
    string? KldBaseFingerprint,
    string? KldBaseLogitsDigest,
    string? ErrorMessage);

/// <summary>One immutable fidelity measurement of one run, as the attempt-history read serves it.</summary>
public sealed record BenchmarkFidelityAttemptRecord(
    Guid Id,
    Guid RunId,
    int Sequence,
    string Kind,
    BenchmarkJudgeAttemptStatus Status,
    double? PerplexityMean,
    double? PerplexityStdErr,
    int? PerplexityChunks,
    int? PerplexityContextTokens,
    string? CorpusId,
    double? KldMean,
    double? KldP99,
    double? TopTokenAgreement,
    string? BaseModelName,
    string? BaseModelContentFingerprint,
    string? BaseLogitsDigest,
    string? ErrorMessage,
    long EnqueuedAtUtc,
    long? StartedAtUtc,
    long? CompletedAtUtc);

/// <summary>
///     What freeze decided one phase of a run would launch with, before anything was spawned. Compared against the
///     evidence the launch itself recorded; the two differing is a fact the UI shows, not an error.
/// </summary>
/// <param name="KvCacheTypeSource"><c>explicit</c> when the run asked for this type, <c>auto</c> when freeze picked it.</param>
/// <param name="KvAutoReason">Why Auto did not pick the quantized type, or <see langword="null" /> when it did.</param>
public sealed record BenchmarkRunLaunchIntent(
    string Variant,
    string KvCacheType,
    string KvCacheTypeSource,
    string? KvAutoReason,
    string FlashAttentionMode,
    string IntendedLaunchIdentity,
    string? IntendedExecutableSha256);

/// <summary>
///     The durable launch evidence recorded for one phase. <see cref="ReceiptJson" /> is null when the spawn never
///     reached readiness — the environment capture is still recorded, because a failed launch is exactly when the
///     host facts matter.
/// </summary>
public sealed record BenchmarkRunLaunchEvidence(
    ReadOnlyMemory<byte>? ReceiptJson,
    ReadOnlyMemory<byte>? EnvironmentFactsJson,
    string? ReceiptHash,
    string? EnvironmentFactsHash,
    string? EffectiveLaunchIdentity,
    string? EffectiveBackend,
    int? PlacementOffloaded,
    int? PlacementTotal,
    string? ExecutableSha256,
    bool? HasAuxAssets,
    string? KvCacheTypeSource);

/// <summary>
///     Everything a run's durable launch-ready checkpoint records about what actually launched: the provider-owned
///     receipt and the pre-launch environment facts (both canonical JSON, encrypted at rest by the store), their
///     hashes, and the flat columns the list/compare views read without decrypting a payload.
/// </summary>
/// <remarks>
///     Deliberately strings, integers and flags only — the list view reads every column here without decrypting or
///     parsing the receipt payload. The receipt is assembled in the llama-server provider and serialized
///     before it reaches the store, so persisting it never drags a provider type through the store contract. Every
///     receipt-derived member is null together when the spawn failed before readiness.
/// </remarks>
public sealed record BenchmarkLaunchReceiptCommand(
    string? ReceiptJson,
    string EnvironmentFactsJson,
    string EnvironmentFactsHash,
    string? ReceiptHash,
    string? EffectiveLaunchIdentity,
    string? EffectiveBackend,
    int? PlacementOffloaded,
    int? PlacementTotal,
    string? ExecutableSha256,
    bool? HasAuxAssets,
    string KvCacheTypeSource);

/// <summary>
///     The run-level judge state the API shows, derived from the run's current attempt and the project's current policy
///     revision. Nothing here is stored: a policy or runtime change must re-derive it, never re-label a stored value.
/// </summary>
/// <param name="State"><c>none</c> when there is no attempt, otherwise the current attempt's status, lowercased.</param>
/// <param name="RankExclusionReason">
///     Why this run is not in the ranked cohort, or <see langword="null" /> when it is ranked. One of <c>no-score</c>,
///     <c>judge-pending</c>, <c>judge-failed</c>, <c>judge-cancelled</c>, <c>policy-outdated</c>,
///     <c>generation-stale</c>, <c>execution-key-mismatch</c>, <c>execution-identity-incomplete</c>, <c>truncated</c>,
///     <c>incomplete</c>, <c>warmup</c>.
/// </param>
public sealed record BenchmarkRunJudgeView(
    string State,
    Guid? AttemptId,
    int? Score,
    int? PolicyRevision,
    Guid? PolicyRevisionId,
    int? AttemptSequence,
    int? CohortGeneration,
    string? ExecutionKey,
    string? ErrorMessage,
    bool PolicyCurrent,
    bool ExecutionCurrent,
    string? RankExclusionReason);

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
    ///     answer token was emitted. Truncated for every consumer (<see cref="IsTruncated" /> covers it), but it names
    ///     the reasoning budget as the thing to raise rather than the output budget, which is the whole difference
    ///     between a run an operator can fix and one they cannot explain.
    /// </summary>
    public const string ReasoningLength = "reasoning-length";

    /// <summary>
    ///     The invocation ended cleanly but produced no answer: the turn stopped on an unanswered tool call, or every
    ///     token it emitted was reasoning. Node-derived, not a provider token — llama-server reports <c>stop</c> or
    ///     <c>tool_calls</c> for both shapes, which read as a finished answer everywhere downstream and let a run that
    ///     answered NOTHING be judged and ranked against runs that did.
    /// </summary>
    public const string Incomplete = "incomplete";

    /// <summary>
    ///     Whether the primary generation stopped because it ran out of budget. <see cref="Length" /> is the
    ///     OpenAI-compatible token for BOTH causes llama-server reports it for — <c>n_predict</c> exhausted and the
    ///     context window full (<c>stopped_limit</c>) — and both mean the same thing here: the answer is cut off.
    ///     <see cref="ReasoningLength" /> is the node's narrowing of the same fact and is therefore also truncated;
    ///     splitting them here would exclude one and rank the other.
    ///     <para>
    ///         One implementation on purpose. Ranking (<c>BenchmarkStore.ApplyRunExclusions</c>) and judging
    ///         (<c>BenchmarkJudgeExecutor</c>) live in different assemblies and used to hold byte-identical private
    ///         copies; a second truncation token added to only one of them would make ranking exclude a run the judge
    ///         was never told was cut off.
    ///     </para>
    /// </summary>
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
    ///     The measurement is still a real one — the run stays <c>Succeeded</c> — but an incomplete answer must not be
    ///     ranked against complete ones, whatever the judge scored it. An operator score still overrides.
    /// </summary>
    public const string ReasonTruncated = "truncated";

    /// <summary>
    ///     The primary generation finished cleanly but produced no answer at all — it stopped on an unanswered tool
    ///     call, or emitted only reasoning. Excluded for the same reason as <see cref="ReasonTruncated" /> and with the
    ///     same operator override: there is nothing for a rubric to grade, so whatever a judge scored it cannot rank
    ///     against runs that answered.
    /// </summary>
    public const string ReasonIncomplete = "incomplete";

    /// <summary>
    ///     A warm-up run. It is a real measurement, kept and shown, but it is exactly the first-launch cost the repeats
    ///     after it were meant NOT to pay — ranking it against them would rank the thing being controlled for. Unlike
    ///     every other reason here, an operator score does not override it: a warm-up is not a contender.
    /// </summary>
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

    /// <summary>Two answers to different questions were never comparable. Unreachable in P2, where a project is one case.</summary>
    public const string ReasonPairwiseCrossCase = "pairwise-cross-case";

    /// <summary>
    ///     A comparison in the fitted set was judged by a runtime other than the one the cohort was claimed with. The
    ///     whole fit is refused rather than fitted over the matching subset: dropping comparisons changes the graph and
    ///     can disconnect it, publishing a number over a set the operator never chose. Re-judging heals it.
    /// </summary>
    public const string ReasonPairwiseExecutionMismatch = "pairwise-execution-mismatch";

    /// <summary>Nothing has promoted a reference execution key yet, so no fit can be shown to belong to a cohort.</summary>
    public const string ReasonPairwiseExecutionIdentityIncomplete = "pairwise-execution-identity-incomplete";
}

/// <summary>
///     What the project's ranking is currently computed against. The UI needs it to say "n of m ranked" honestly —
///     a project mid-re-judge, or one whose judge runtime moved, shows fewer ranked rows on purpose.
/// </summary>
public sealed record BenchmarkRankCohort(
    int? PolicyRevision,
    string? ExecutionKey,
    int? CohortGeneration,
    int RankedCount,
    int TotalScored);

/// <param name="TotalCount">Runs matching the filter, not in this page.</param>
public sealed record BenchmarkRunPage(IReadOnlyList<BenchmarkRunRecord> Items, int TotalCount, BenchmarkRankCohort? RankCohort = null);

/// <summary>Where a run's ranking value came from.</summary>
public static class BenchmarkQualityScoreSources
{
    public const string User = "user";
    public const string Judge = "judge";

    /// <summary>The Bradley–Terry strength read out of the cohort's active fit, in a project judging pairwise.</summary>
    public const string Pairwise = "pairwise";

    public const string None = "none";
}

public sealed record BenchmarkClaimedWork(
    long QueueSequence,
    Guid RunId,
    BenchmarkWorkKind Kind,
    int Attempt,
    long Version,
    BenchmarkRunRecord Run,
    Guid? JudgeAttemptId = null,
    Guid? FidelityAttemptId = null,
    Guid? ComparisonId = null);

public abstract class BenchmarkStoreException(string message) : InvalidOperationException(message);

public sealed class BenchmarkNotFoundException(string message) : BenchmarkStoreException(message);

public sealed class BenchmarkConflictException(string code) : BenchmarkStoreException(code)
{
    public string Code { get; } = code;
}

public sealed class BenchmarkValidationException(string message) : BenchmarkStoreException(message);

/// <summary>
///     The project's judge policy moved while a judging was being prepared for the previous revision. Retryable: the
///     caller re-reads the current revision, re-resolves the judge runtime and calls again.
/// </summary>
public sealed class BenchmarkJudgePolicyChangedException(string message) : BenchmarkStoreException(message);
