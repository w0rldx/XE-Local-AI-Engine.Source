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
