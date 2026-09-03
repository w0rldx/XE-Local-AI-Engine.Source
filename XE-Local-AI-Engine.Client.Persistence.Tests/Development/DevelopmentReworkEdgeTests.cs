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
public sealed class DevelopmentReworkEdgeTests : IDisposable
{
    private const string Reason = "The validate node rejected this implementation: 3 of 15 tests failed.";

    private const string Policy = "## Policy: House rules\nNever touch production without an approved plan.";

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
        var ruleSets = new[] { new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash") };

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
        var ruleSets = new[] { new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash") };

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
                           DevelopmentTaskStatus.InProgress,
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
