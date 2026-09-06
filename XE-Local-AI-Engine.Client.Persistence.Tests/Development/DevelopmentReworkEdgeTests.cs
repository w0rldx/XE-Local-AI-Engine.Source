namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The edge back out of <c>AwaitingApply</c>, and what it leaves behind.
///     <para>
///         An approved task had nowhere to go but Blocked or Cancelled, so a workflow fix loop routed at the node that
///         implemented it could only re-succeed against work nothing had asked to be changed. Asking for rework is now
///         a legal transition — and the transition is where the task stops carrying its approval, where the reason is
///         recorded in the casing every reader expects, and where the next round's brief comes from.
///     </para>
/// </summary>
// SqliteFileProbe.ReleasePooledHandles is SqliteConnection.ClearAllPools, which is process-global: one test's
// teardown kills a sibling's in-flight connection. Latent while the class was small, reproducible once it was not.
[NotInParallel]
public sealed class DevelopmentReworkEdgeTests : IDisposable
{
    private const string Reason = "The validate node rejected this implementation: 3 of 15 tests failed.";

    private const string Policy = "## Policy: House rules\nNever touch production without an approved plan.";

    private const string GateReason =
        "Deterministic validation failed (tests_failed): Command dotnet_test_release_no_build reported 1 failing of 3 executed tests.";

    private const string OperatorReason =
        "An operator retried the 'implement' step of the workflow driving this task, and said: keep the Square test in its own new file.";

    private const string RoundLimitReason = "The configured maximum number of rounds has been reached.";

    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    [Test]
    public async Task AnApprovedTaskCanBeSentBackForRework_AndStopsCarryingTheApprovedSubject()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        AssertEx.Equal("subject", await ApprovedSubjectHashAsync(dbContext, seed.TaskId).ConfigureAwait(false));

        var moved = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   Reason))
                               .ConfigureAwait(false);

        AssertEx.Equal(nameof(DevelopmentTaskStatus.ChangesRequested), moved.Status);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Status);
        AssertEx.Null(await ApprovedSubjectHashAsync(dbContext, seed.TaskId).ConfigureAwait(false),
            "a task asked for rework is not an approved one, so it stops carrying the subject a review approved.");

        // Completion is still the apply port's alone: widening AwaitingApply must not have opened a generic route to it.
        _ = await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() =>
                              store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                  Guid.NewGuid(),
                                  DevelopmentTaskStatus.Completed,
                                  moved.Version)))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The reason is written camelCase, like every other document this product puts on a wire, and it is what the
    ///     next coder attempt is composed from — so the round that has to fix the work is told what was wrong with it.
    /// </summary>
    [Test]
    public async Task TheReworkReasonIsWrittenInCamelCaseAndBecomesTheNextRoundsFeedback()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        var moved = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   Reason))
                               .ConfigureAwait(false);

        var written = await dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == seed.TaskId && entity.EventType == "TaskTransitioned")
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstAsync()
                                     .ConfigureAwait(false);
        AssertEx.Equal($$"""{"reason":"{{Reason}}"}""", Encoding.UTF8.GetString(written.DetailJson!));
        var ledger = Encoding.UTF8.GetString(written.ResultMetadataJson!);
        AssertEx.True(ledger.Contains("\"projectId\":", StringComparison.Ordinal) && ledger.Contains("\"status\":\"ChangesRequested\"", StringComparison.Ordinal),
            $"the operation ledger the store reads back for an idempotent replay is written in the same casing: {ledger}");

        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           moved.Version))
                       .ConfigureAwait(false);

        AssertEx.Equal(Reason,
            (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).PreviousRoundFeedback,
            "the rework round's own execution snapshot is where the coder prompt reads the previous round from.");
    }

    /// <summary>
    ///     P2, live 2026-09-04. An operator's amendment has to reach the REVIEWER, and it has to still be there several
    ///     hops later — the round it corrects is the review, not the coder round it starts. It is also not read as the
    ///     previous round's feedback, so the prompts that rank the two can never render the same sentence twice.
    ///     <para>
    ///         Free of the status gate on purpose: a Dev Mode task's requirements are immutable, so this is the only
    ///         channel that can amend one, and an amendment that expired at the next event would be undone by the
    ///         reviewer it exists to correct — which is exactly the deadlock it was written for. What DOES end it is
    ///         the node run that made it, which the test below this one pins.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnOperatorsAmendmentReachesTheReviewersSnapshotAndIsNotTheRoundsFeedback()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        var asked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   OperatorReason,
                                   OperatorDirected: true))
                               .ConfigureAwait(false);

        // The whole way round to the next review: the coder round the retry asked for, its gate, and the review.
        foreach (var status in new[]
                 {
                     DevelopmentTaskStatus.InProgress,
                     DevelopmentTaskStatus.Validation,
                     DevelopmentTaskStatus.InReview
                 })
        {
            asked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), status, asked.Version)).ConfigureAwait(false);
        }

        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Reviewer,
                           "local-model",
                           "local",
                           asked.Version))
                       .ConfigureAwait(false);

        var snapshot = await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false);
        AssertEx.Equal(OperatorReason,
            snapshot.OperatorInstruction,
            "the reviewer judged against requirements the operator had already amended, and sent the amendment straight back.");
        AssertEx.Null(snapshot.PreviousRoundFeedback,
            "a person's sentence answers one field or the other, never both, or a prompt that ranks them renders it twice.");
    }

    /// <summary>
    ///     The amendment is bounded by the node run that made it, exactly as the workflow's policy text beside it is,
    ///     and by the same row. Without that bound an operator's "skip the flaky auth test for now" on retry 1 would
    ///     govern every later round of the task, and every later reviewer would be told not to ask for it back — which
    ///     permanently disarms the reward-hacking control with no route to withdraw it.
    /// </summary>
    [Test]
    public async Task AnOperatorsAmendmentStopsGoverningWhenTheNodeRunThatMadeItSettles()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var ruleSets = new[]
        {
            new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash")
        };

        // The node run's dispatch, then the operator's Retry inside it, then the round it asked for.
        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), Policy, ruleSets).ConfigureAwait(false);
        var attemptId = await ReworkThenStartCoderAttemptAsync(store, seed.TaskId, version, OperatorReason, operatorDirected: true).ConfigureAwait(false);

        AssertEx.Equal(OperatorReason,
            (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).OperatorInstruction,
            "the round the operator paid for is inside the node run that wrote the instruction.");

        // The clear every terminal path of a node run writes.
        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), string.Empty, []).ConfigureAwait(false);

        AssertEx.Null((await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).OperatorInstruction,
            "what a person told one node run must not go on outranking the requirements of every round after it.");
    }

    /// <summary>
    ///     The withdrawal. An operator-directed row carrying NO reason retracts the instruction the operator wrote
    ///     earlier in the same dispatch, rather than being skipped as "not an operator row" — which is what the
    ///     <c>DetailJson != null</c> filter used to make it, leaving an amendment that outranks the requirements and
    ///     disarms the reviewer with no route back out inside the node run that wrote it.
    /// </summary>
    [Test]
    public async Task ABlankReasonOperatorRowWithdrawsTheInstructionBeforeIt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        var attemptId = await ReworkThenStartCoderAttemptAsync(store, seed.TaskId, version, OperatorReason, operatorDirected: true).ConfigureAwait(false);
        AssertEx.Equal(OperatorReason, (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).OperatorInstruction);

        // What an empty Retry box writes: the same operator-directed transition, with nothing said in it.
        var asked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                                   Reason: null,
                                   OperatorDirected: true))
                               .ConfigureAwait(false);

        AssertEx.Null((await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).OperatorInstruction,
            "a person who takes their amendment back must stop outranking the requirements from that moment on.");
        AssertEx.Null((await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).PreviousRoundFeedback,
            "and the withdrawal is not itself feedback for the round to answer.");

        // A LATER instruction still governs: the withdrawal is not a permanent kill, exactly as a blank policy is not.
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.InProgress,
                           asked.Version))
                       .ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.ChangesRequested,
                           (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                           OperatorReason,
                           OperatorDirected: true))
                       .ConfigureAwait(false);
        AssertEx.Equal(OperatorReason, (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).OperatorInstruction);
    }

    /// <summary>
    ///     The rework sentence stops being the CURRENT one when the round it asked for starts. Only
    ///     <c>TransitionTaskAsync</c> cleared it; the coder round reaches <c>InProgress</c> through
    ///     <c>StartAttemptAsync</c> instead, which never touched the column — so the Development overview, which
    ///     renders it with no status gate, showed the gate failure or the operator's change request against a task
    ///     that was already being reworked because of it.
    /// </summary>
    [Test]
    public async Task TheCoderRoundStartingClearsTheReasonThatAskedForIt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        var asked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   GateReason))
                               .ConfigureAwait(false);
        AssertEx.Equal(GateReason,
            (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).BlockedReason,
            "the reason is the operator's answer to 'why is this being reworked' right up to the round that answers it.");

        // The coder round the change request asked for, started the way the chain starts one: straight off
        // ChangesRequested, with no TransitionTaskAsync hop in between.
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           Guid.NewGuid(),
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           asked.Version))
                       .ConfigureAwait(false);

        var reworking = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, reworking.Status);
        AssertEx.Null(reworking.BlockedReason, "the round that acts on the complaint is where it stops being the current one.");
    }

    /// <summary>
    ///     The fall-through the exclusion opens, and the whole point of ranking the two fields: with a reviewer's
    ///     complaint behind the operator's amendment, the round is told BOTH — the operator first and above, the
    ///     reviewer below — rather than the operator's sentence shadowing a complaint nobody has answered yet.
    /// </summary>
    [Test]
    public async Task AnOperatorsAmendmentDoesNotShadowTheReviewerComplaintItOverrides()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        // Round N: the reviewer asks for changes. Round N's coder round runs and is refused, leaving the task where a
        // Retry can reach it, and the operator overrides the reviewer.
        var reviewed = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                      Guid.NewGuid(),
                                      DevelopmentTaskStatus.ChangesRequested,
                                      version,
                                      Reason))
                                  .ConfigureAwait(false);
        var refused = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                     Guid.NewGuid(),
                                     DevelopmentTaskStatus.InProgress,
                                     reviewed.Version))
                                 .ConfigureAwait(false);
        var attemptId = await ReworkThenStartCoderAttemptAsync(store, seed.TaskId, refused.Version, OperatorReason, operatorDirected: true).ConfigureAwait(false);

        var snapshot = await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false);
        AssertEx.Equal(OperatorReason, snapshot.OperatorInstruction);
        AssertEx.Equal(Reason,
            snapshot.PreviousRoundFeedback,
            "the complaint the operator is overriding is still what the round has to answer, so the round must be able to read it.");
    }

    /// <summary>
    ///     P1, live 2026-09-05. The round cap is the ONE thing about a Dev Mode task an operator can change, and a
    ///     Retry buys exactly one round of it. The widening is also what pays for the single edge out of
    ///     <c>Blocked</c>: without it the task would be handed a round it has no budget to finish, which is the
    ///     two-second re-block the live round measured twice over.
    /// </summary>
    [Test]
    public async Task AnOperatorRetryWidensTheRoundCapByOne_AndIsTheOnlyEdgeOutOfBlocked()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var blocked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                     Guid.NewGuid(),
                                     DevelopmentTaskStatus.Blocked,
                                     version,
                                     RoundLimitReason))
                                 .ConfigureAwait(false);
        AssertEx.Equal(expected: 3, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).MaxReviewRounds);

        _ = await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() =>
                              store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                  Guid.NewGuid(),
                                  DevelopmentTaskStatus.ChangesRequested,
                                  blocked.Version,
                                  OperatorReason,
                                  OperatorDirected: true)))
                          .ConfigureAwait(false);

        var widened = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                     Guid.NewGuid(),
                                     DevelopmentTaskStatus.ChangesRequested,
                                     blocked.Version,
                                     OperatorReason,
                                     OperatorDirected: true,
                                     WidenReviewRounds: true))
                                 .ConfigureAwait(false);

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);
        AssertEx.Equal(expected: 4, task.MaxReviewRounds, "the Retry bought one round, not an exemption from the budget.");
        AssertEx.Equal(OperatorReason, task.BlockedReason, "the sentence the round has to act on is the operator's, and it replaces the cap's.");
        AssertEx.Null(task.BlockedAtUtc, "the task is no longer stood down, so nothing is timing a stand-down.");

        var written = await dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == seed.TaskId && entity.EventType == "TaskTransitioned")
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstAsync()
                                     .ConfigureAwait(false);
        AssertEx.Equal(nameof(DevelopmentTaskStatus.ChangesRequested), widened.Status);
        AssertEx.Equal("TransitionedByOperator", written.Outcome, "a widening is a person's decision, and the audit row says whose.");
        var detail = Encoding.UTF8.GetString(written.DetailJson!);
        AssertEx.True(detail.StartsWith("{\"reason\":\"", StringComparison.Ordinal) && detail.Contains("keep the Square test in its own new file.", StringComparison.Ordinal),
            $"the operator's sentence is what the next round reads, in the one detail shape every reader of this store expects: {detail}");
    }

    /// <summary>
    ///     The widening is a write like any other and is refused on a stale read: two ticks racing the same blocked
    ///     task must buy ONE round between them, not one each.
    /// </summary>
    [Test]
    public async Task AWideningOnAStaleVersion_IsRefusedAndLeavesTheCapWhereItWas()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var blocked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                     Guid.NewGuid(),
                                     DevelopmentTaskStatus.Blocked,
                                     version,
                                     RoundLimitReason))
                                 .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevelopmentConcurrencyException>(() =>
                              store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                  Guid.NewGuid(),
                                  DevelopmentTaskStatus.ChangesRequested,
                                  blocked.Version - 1,
                                  OperatorReason,
                                  OperatorDirected: true,
                                  WidenReviewRounds: true)))
                          .ConfigureAwait(false);

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, task.Status);
        AssertEx.Equal(expected: 3, task.MaxReviewRounds, "a refused write buys nothing.");
    }

    /// <summary>
    ///     A Retry written by the build before this change carries the plain outcome, so it answers the old field and
    ///     not the new one — the previous behaviour, unchanged, for any task in flight across the upgrade. Graceful
    ///     degradation by construction rather than by migration, which is why it is pinned rather than fixed.
    /// </summary>
    [Test]
    public async Task ARetryRecordedBeforeThisChangeIsStillReadAsTheRoundsFeedback()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        // Byte-for-byte what the older build wrote: the same operator sentence, without the flag that marks it.
        var attemptId = await ReworkThenStartCoderAttemptAsync(store, seed.TaskId, version, OperatorReason, operatorDirected: false).ConfigureAwait(false);

        var snapshot = await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false);
        AssertEx.Null(snapshot.OperatorInstruction, "nothing recorded it as a person's, and this store does not guess from the sentence.");
        AssertEx.Equal(OperatorReason, snapshot.PreviousRoundFeedback, "so it keeps reaching the round exactly as it did before.");
    }

    /// <summary>Asks for a rework round with the given reason, then starts the coder attempt it asked for.</summary>
    private static async Task<Guid> ReworkThenStartCoderAttemptAsync(IDevelopmentStore store,
        Guid taskId,
        long version,
        string reason,
        bool operatorDirected)
    {
        var asked = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   reason,
                                   OperatorDirected: operatorDirected))
                               .ConfigureAwait(false);
        var running = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                     Guid.NewGuid(),
                                     DevelopmentTaskStatus.InProgress,
                                     asked.Version))
                                 .ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(taskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           running.Version))
                       .ConfigureAwait(false);
        return attemptId;
    }

    /// <summary>
    ///     The rework sentence is kept ON the task, not only in the event log, so the page that shows a task has
    ///     something to show. The column is named for the stand-down case that came first; it now also carries "why
    ///     changes were asked for", and the next round's hop clears it the same way it always cleared a stand-down.
    /// </summary>
    [Test]
    public async Task TheReworkReasonIsKeptOnTheTaskUntilTheNextRoundStarts()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        var moved = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   Reason))
                               .ConfigureAwait(false);

        var reworked = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(Reason, reworked.BlockedReason, "a task asked for rework carries why it was asked, not only an event row saying so.");
        AssertEx.Null(reworked.BlockedAtUtc, "asking for rework is not a stand-down, so nothing times one.");

        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.InProgress,
                           moved.Version))
                       .ConfigureAwait(false);

        AssertEx.Null((await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).BlockedReason,
            "the round that acts on the complaint is where it stops being the current one.");
    }

    /// <summary>
    ///     The other event-derived channel into a round's brief: the rule-set text a Development workflow resolved for
    ///     the node run driving this task. Written once per operation id and read back off the task's own log, so a
    ///     re-bound node run replaying the same injection cannot append a second one for the snapshot to prefer.
    /// </summary>
    [Test]
    public async Task AWorkflowsPolicyIsRecordedOncePerOperationAndBecomesTheRoundsPolicyText()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var ruleSets = new[]
        {
            new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash")
        };

        var first = await store.RecordWorkflowPolicyAsync(seed.TaskId, operationId, Policy, ruleSets).ConfigureAwait(false);
        var replayed = await store.RecordWorkflowPolicyAsync(seed.TaskId, operationId, "Something else entirely.", ruleSets).ConfigureAwait(false);

        AssertEx.Equal(first.Sequence, replayed.Sequence, "the same operation id answers with what it already did rather than injecting a second policy.");
        AssertEx.Equal(expected: 1,
            await dbContext.DevelopmentEvents.AsNoTracking().CountAsync(entity => entity.TaskId == seed.TaskId && entity.EventType == "WorkflowPolicyApplied")
                           .ConfigureAwait(false),
            "one row, whatever the replay was handed.");

        // The round the policy governs: a coder attempt only starts once the task is back where one runs.
        var reworking = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.ChangesRequested, version))
                                   .ConfigureAwait(false);
        var inProgress = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                        Guid.NewGuid(),
                                        DevelopmentTaskStatus.InProgress,
                                        reworking.Version))
                                    .ConfigureAwait(false);

        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           inProgress.Version))
                       .ConfigureAwait(false);

        AssertEx.Equal(Policy,
            (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText,
            "the round's own execution snapshot is where the coder and reviewer prompts read the workflow's policy from.");
    }

    /// <summary>
    ///     What bounds the injection in time: a policy event with BLANK text revokes the one before it. The snapshot
    ///     answers off the latest row, so both the settle's explicit clear and a later workflow that resolved nothing
    ///     stop the earlier policy governing rounds it was never applied to — and no second event type is needed to say
    ///     it. A clear that named rule sets would be claiming an injection, so the store refuses one.
    /// </summary>
    [Test]
    public async Task ABlankWorkflowPolicyRevokesTheOneBeforeItAndCannotNameRuleSets()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var ruleSets = new[]
        {
            new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash")
        };

        var reworking = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.ChangesRequested, version))
                                   .ConfigureAwait(false);
        var inProgress = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                        Guid.NewGuid(),
                                        DevelopmentTaskStatus.InProgress,
                                        reworking.Version))
                                    .ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           inProgress.Version))
                       .ConfigureAwait(false);

        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), Policy, ruleSets).ConfigureAwait(false);
        AssertEx.Equal(Policy, (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText);

        // The clear a settling node run writes.
        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), string.Empty, []).ConfigureAwait(false);
        AssertEx.Null((await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText,
            "a cleared policy governs nothing that comes after it.");

        // A later node run that DOES resolve one governs again — the clear is not a permanent kill.
        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), Policy, ruleSets).ConfigureAwait(false);
        AssertEx.Equal(Policy, (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText);

        // And a later node run that resolves NOTHING records an empty applied event, which reads the same as a clear.
        _ = await store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), "   ", []).ConfigureAwait(false);
        AssertEx.Null((await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText,
            "a workflow that resolved no policy must not leave the previous one governing.");

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), string.Empty, ruleSets),
                              "a clear that named rule sets would be claiming an injection it is not making.")
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => store.RecordWorkflowPolicyAsync(seed.TaskId, Guid.NewGuid(), Policy, []),
                              "and an injection still has to name what composed it.")
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The hop that starts the new round is the hop that invalidates the old evidence, and it happens BEFORE any
    ///     attempt can read it: the interim <c>ChangesRequested</c> window leaves the reports alone, and
    ///     <c>ChangesRequested → InProgress</c> — which <c>StartNextActionAsync</c> makes before it starts a coder
    ///     attempt — marks them stale.
    /// </summary>
    [Test]
    public async Task TheHopIntoTheNewRoundInvalidatesTheStaleEvidenceBeforeAnAttemptCanReadIt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var artifactId = Guid.NewGuid();
        _ = await store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand(artifactId,
                           seed.ProjectId,
                           seed.TaskId,
                           AttemptId: null,
                           Guid.NewGuid(),
                           DevelopmentArtifactKind.ValidationReport,
                           SchemaVersion: 1,
                           "content-hash",
                           ByteCount: 2,
                           ContentJson: Encoding.UTF8.GetBytes("{}")))
                       .ConfigureAwait(false);

        var moved = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   DevelopmentTaskStatus.ChangesRequested,
                                   version,
                                   Reason))
                               .ConfigureAwait(false);
        AssertEx.True(await IsValidAsync(dbContext, artifactId).ConfigureAwait(false),
            "a task waiting for a new round has not produced anything to supersede the old report with yet.");

        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.InProgress,
                           moved.Version))
                       .ConfigureAwait(false);

        AssertEx.False(await IsValidAsync(dbContext, artifactId).ConfigureAwait(false),
            "the previous round's validation report describes an implementation that is being replaced.");
    }

    /// <summary>
    ///     The LATEST rework reason is what the next round is told, whichever stage wrote it. A reviewer asks for
    ///     changes, the round it asked for fails the deterministic gate — and the gate's complaint is the newer fact,
    ///     so replaying the reviewer's sentence would hand the coder round N-1's objection to a patch round N has
    ///     already superseded.
    /// </summary>
    [Test]
    public async Task AValidationFailureAfterAReviewersReworkIsWhatTheNextRoundIsTold()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);

        async Task<long> VersionAsync() =>
            (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version;

        async Task MoveAsync(DevelopmentTaskStatus target, string? reason = null) =>
            _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                               Guid.NewGuid(),
                               target,
                               await VersionAsync().ConfigureAwait(false),
                               reason))
                           .ConfigureAwait(false);

        // Round one reaches review, and the reviewer asks for changes.
        await MoveAsync(DevelopmentTaskStatus.Ready).ConfigureAwait(false);
        await MoveAsync(DevelopmentTaskStatus.InProgress).ConfigureAwait(false);
        await MoveAsync(DevelopmentTaskStatus.Validation).ConfigureAwait(false);
        await MoveAsync(DevelopmentTaskStatus.InReview).ConfigureAwait(false);
        await MoveAsync(DevelopmentTaskStatus.ChangesRequested, "The reviewer wants the inverted range covered.").ConfigureAwait(false);

        // Round two produces a patch the deterministic gate then rejects.
        await MoveAsync(DevelopmentTaskStatus.InProgress).ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        var attempt = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                     attemptId,
                                     Guid.NewGuid(),
                                     DevelopmentAttemptRole.Coder,
                                     "local-model",
                                     "local",
                                     await VersionAsync().ConfigureAwait(false)))
                                 .ConfigureAwait(false);
        _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptStatus.Succeeded,
                           attempt.Version))
                       .ConfigureAwait(false);
        await MoveAsync(DevelopmentTaskStatus.Validation).ConfigureAwait(false);
        _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(new DevelopmentAttachArtifactCommand(Guid.NewGuid(),
                               seed.ProjectId,
                               seed.TaskId,
                               attemptId,
                               Guid.NewGuid(),
                               DevelopmentArtifactKind.ValidationReport,
                               SchemaVersion: 1,
                               "content-hash",
                               ByteCount: 2,
                               ContentJson: Encoding.UTF8.GetBytes("{}")),
                           Guid.NewGuid(),
                           await VersionAsync().ConfigureAwait(false),
                           DevelopmentTaskStatus.ChangesRequested,
                           "The release test command reported 3 failing tests."))
                       .ConfigureAwait(false);

        var next = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           next,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           await VersionAsync().ConfigureAwait(false)))
                       .ConfigureAwait(false);

        AssertEx.Equal("The release test command reported 3 failing tests.",
            (await store.GetExecutionSnapshotAsync(next).ConfigureAwait(false)).PreviousRoundFeedback,
            "the gate's complaint is newer than the reviewer's, so it is the one the round has to act on.");
    }

    /// <summary>
    ///     The FAILED deterministic gate's hop, and the livelock it exists to close.
    ///     <para>
    ///         The gate used to return the task to <c>InProgress</c>, which is byte-for-byte the state that means
    ///         "implemented, validate it" — a succeeded coder attempt with no current evidence — so the next action was
    ///         the same validation again. Measured live on 2026-09-04: 289 restore/build/test runs on one task in 25
    ///         minutes, 282 validation-report rows, zero coder rounds, ended only by cancelling the run.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AFailedDeterministicGateAsksTheCoderForANewRoundAndSpendsAReviewRound()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, attemptId) = await SeedTaskInValidationAsync(store).ConfigureAwait(false);

        // An earlier round's report, valid until this hop supersedes it.
        var staleId = Guid.NewGuid();
        _ = await store.AttachArtifactAsync(ValidationArtifact(staleId, seed, attemptId)).ConfigureAwait(false);

        var reportId = Guid.NewGuid();
        var finalized = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(reportId, seed, attemptId),
                                       Guid.NewGuid(),
                                       (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                                       DevelopmentTaskStatus.ChangesRequested,
                                       GateReason))
                                   .ConfigureAwait(false);

        AssertEx.Equal(nameof(DevelopmentTaskStatus.ChangesRequested), finalized.Status);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested,
            task.Status,
            "a failed gate hands the failure to the coder; InProgress asked for the same validation again.");
        AssertEx.Equal(expected: 1, task.CurrentReviewRound, "and it spends a round, exactly as a reviewer's rejection does.");
        AssertEx.Equal(GateReason, task.BlockedReason, "the operator-facing copy of the reason names what the gate found.");
        AssertEx.Null(task.ApprovedSubjectHash);
        AssertEx.False(await IsValidAsync(dbContext, staleId).ConfigureAwait(false), "the superseded report is marked stale on this hop.");
        AssertEx.False(await IsValidAsync(dbContext, reportId).ConfigureAwait(false), "and a failing report is never current evidence.");

        // The whole point of the hop: the next action off this status is a CODER round, which is the one thing the
        // old target could not be. The reviewer still cannot start, because nothing has been validated.
        var next = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           next,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           task.Version))
                       .ConfigureAwait(false);
        AssertEx.Equal(GateReason,
            (await store.GetExecutionSnapshotAsync(next).ConfigureAwait(false)).PreviousRoundFeedback,
            "and the round is told what the gate found, or it re-implements blind.");
    }

    /// <summary>
    ///     The round count is bounded by construction. Reaching the cap inside a validation is unreachable on the live
    ///     path — the management service stands a task down at the cap BEFORE it schedules one — and this store method
    ///     is callable on its own, so the branch has to answer rather than overrun: the task still lands at
    ///     <c>ChangesRequested</c> carrying the reason, and the stand-down arrives off a count already at its limit.
    /// </summary>
    [Test]
    public async Task AFailedGateNeverSpendsMoreRoundsThanTheTaskHas()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, attemptId) = await SeedTaskInValidationAsync(store, maxReviewRounds: 1).ConfigureAwait(false);

        for (var round = 0; round < 2; round++)
        {
            if (round > 0)
            {
                // Back through the coder round the previous failure asked for, and into the gate again.
                await MoveToValidationAsync(store, seed.TaskId, attemptId).ConfigureAwait(false);
            }

            _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(Guid.NewGuid(), seed, attemptId),
                               Guid.NewGuid(),
                               (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                               DevelopmentTaskStatus.ChangesRequested,
                               GateReason))
                           .ConfigureAwait(false);
        }

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);
        AssertEx.Equal(expected: 1, task.CurrentReviewRound, "the count stops at the budget rather than running past it.");
        AssertEx.Equal(task.MaxReviewRounds, task.CurrentReviewRound);
    }

    /// <summary>A PASSING gate is unchanged: into review, spending the round the review is about to use.</summary>
    [Test]
    public async Task APassingDeterministicGateStillEntersReviewWithItsEvidenceCurrent()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var (seed, attemptId) = await SeedTaskInValidationAsync(store).ConfigureAwait(false);

        var reportId = Guid.NewGuid();
        _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(reportId, seed, attemptId),
                           Guid.NewGuid(),
                           (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                           DevelopmentTaskStatus.InReview))
                       .ConfigureAwait(false);

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(expected: 1, task.CurrentReviewRound);
        AssertEx.Null(task.BlockedReason);
        AssertEx.True(await IsValidAsync(dbContext, reportId).ConfigureAwait(false), "a passing report IS the evidence the review reads.");
    }

    /// <summary>
    ///     <c>InProgress</c> is refused as a validation verdict outright, which is what makes the livelock unreachable
    ///     rather than merely unused: the store will not park a judged round back in the state that means "validate me".
    /// </summary>
    [Test]
    public async Task AValidationCannotFinalizeBackIntoTheStateThatMeansValidateMe()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, attemptId) = await SeedTaskInValidationAsync(store).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() =>
                              store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(Guid.NewGuid(), seed, attemptId),
                                  Guid.NewGuid(),
                                  // Any value: the argument guard fires before EnsureVersion ever reads it.
                                  ExpectedTaskVersion: 0,
                                  DevelopmentTaskStatus.InProgress,
                                  GateReason)))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The failure sentence dies with the failure. Nothing else on the recovery path clears
    ///     <c>blocked_reason</c> — not the coder round <c>StartAttemptAsync</c> starts, not <c>FinalizeReviewAsync</c>,
    ///     not <c>CompleteApplyAsync</c> — and the Development overview renders it with NO status gate, so a task that
    ///     failed its gate once carried "Deterministic validation failed" under a green approved badge for the rest of
    ///     its life.
    /// </summary>
    [Test]
    public async Task ThePassingGateClearsTheFailureSentenceTheFailingOneWrote()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, attemptId) = await SeedTaskInValidationAsync(store).ConfigureAwait(false);

        _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(Guid.NewGuid(), seed, attemptId),
                           Guid.NewGuid(),
                           (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                           DevelopmentTaskStatus.ChangesRequested,
                           GateReason))
                       .ConfigureAwait(false);
        AssertEx.Equal(GateReason, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).BlockedReason);

        // The rework round, then a gate that passes.
        await MoveToValidationAsync(store, seed.TaskId, attemptId).ConfigureAwait(false);
        _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(ValidationArtifact(Guid.NewGuid(), seed, attemptId),
                           Guid.NewGuid(),
                           (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version,
                           DevelopmentTaskStatus.InReview))
                       .ConfigureAwait(false);

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Null(task.BlockedReason, "an operator reading an approved task must not be shown the failure it recovered from.");
    }

    /// <summary>A project whose single task sits in <c>Validation</c> behind a succeeded coder attempt.</summary>
    private static async Task<(DevelopmentCreateProjectCommand Seed, Guid AttemptId)> SeedTaskInValidationAsync(IDevelopmentStore store,
        int maxReviewRounds = 3)
    {
        var seed = DevelopmentTestFixture.CreateSeed() with
        {
            MaxReviewRounds = maxReviewRounds
        };
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.Ready,
                           ExpectedTaskVersion: 1))
                       .ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        await MoveToValidationAsync(store, seed.TaskId, attemptId).ConfigureAwait(false);
        return (seed, attemptId);
    }

    /// <summary>
    ///     Runs one coder round to success and opens the gate on it. <paramref name="attemptId" /> is started only
    ///     once — a second call reuses the attempt already on the task, which is what a re-validated round would.
    /// </summary>
    private static async Task MoveToValidationAsync(IDevelopmentStore store, Guid taskId, Guid attemptId)
    {
        if ((await store.ListAttemptsAsync(taskId).ConfigureAwait(false)).All(attempt => attempt.Id != attemptId))
        {
            var attempt = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(taskId,
                                         attemptId,
                                         Guid.NewGuid(),
                                         DevelopmentAttemptRole.Coder,
                                         "local-model",
                                         "local",
                                         (await store.GetTaskAsync(taskId).ConfigureAwait(false)).Version))
                                     .ConfigureAwait(false);
            _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attemptId,
                               Guid.NewGuid(),
                               DevelopmentAttemptStatus.Succeeded,
                               attempt.Version))
                           .ConfigureAwait(false);
        }
        else
        {
            _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                               Guid.NewGuid(),
                               DevelopmentTaskStatus.InProgress,
                               (await store.GetTaskAsync(taskId).ConfigureAwait(false)).Version))
                           .ConfigureAwait(false);
        }

        _ = await store.StartValidationAsync(new DevelopmentStartValidationCommand(taskId,
                           Guid.NewGuid(),
                           (await store.GetTaskAsync(taskId).ConfigureAwait(false)).Version))
                       .ConfigureAwait(false);
    }

    private static DevelopmentAttachArtifactCommand ValidationArtifact(Guid artifactId, DevelopmentCreateProjectCommand seed, Guid attemptId) =>
        new(artifactId,
            seed.ProjectId,
            seed.TaskId,
            attemptId,
            Guid.NewGuid(),
            DevelopmentArtifactKind.ValidationReport,
            SchemaVersion: 1,
            "content-hash",
            ByteCount: 2,
            ContentJson: Encoding.UTF8.GetBytes("{}"));

    private static async Task<string?> ApprovedSubjectHashAsync(NodeChatDbContext dbContext, Guid taskId) =>
        await dbContext.DevelopmentTasks.AsNoTracking()
                       .Where(entity => entity.Id == taskId)
                       .Select(entity => entity.ApprovedSubjectHash)
                       .SingleAsync()
                       .ConfigureAwait(false);

    private static async Task<bool> IsValidAsync(NodeChatDbContext dbContext, Guid artifactId) =>
        await dbContext.DevelopmentArtifacts.AsNoTracking()
                       .Where(entity => entity.Id == artifactId)
                       .Select(entity => entity.IsValid)
                       .SingleAsync()
                       .ConfigureAwait(false);
}
