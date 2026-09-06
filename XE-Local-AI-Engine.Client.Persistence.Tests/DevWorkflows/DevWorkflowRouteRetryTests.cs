namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The cross-node fix loop's write, at the level where its atomicity is decided.
///     <para>
///         A route resets the whole subtree under the node it re-runs. Committed a row at a time it left a window in
///         which the failed check was <c>Pending</c> again while the verification and the answered gate beside it still
///         read <c>Succeeded</c> — and nothing reconciles that afterwards, because startup recovery only judges rows
///         left <c>Queued</c> or <c>Running</c>. The run would repeat the check and complete on evidence and an
///         approval about an implementation that no longer existed. These tests hold the write to all-or-nothing.
///     </para>
/// </summary>
public sealed class DevWorkflowRouteRetryTests
{
    private static readonly Guid VerifyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IntegrateId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FullValidateId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Test]
    public async Task ARoute_CommitsItsEventAndEveryResetTogether()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await SeedRoundOneAsync(store).ConfigureAwait(false);

        _ = await store.RouteRetryAsync(RouteFrom(seed.RunId, Guid.NewGuid(), [VerifyId, IntegrateId, FullValidateId])).ConfigureAwait(false);

        var nodeRuns = (await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).ToDictionary(row => row.NodeKey, StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     "verify",
                     "integrate",
                     "fullvalidate"
                 })
        {
            AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[key].Status, $"'{key}' is under the node being re-run, so the route resets it.");
            AssertEx.Equal(expected: 2, nodeRuns[key].Attempt, $"'{key}' spends an attempt on the new round.");
            AssertEx.Null(nodeRuns[key].TerminalReason, $"'{key}' is about to run again, so it must not still report the last round's outcome.");
        }

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryRouted), "The decision is recorded exactly once.");
        AssertEx.Equal(DevWorkflowEventTypes.NodeRetryRouted,
            events.Where(item => item.EventType is DevWorkflowEventTypes.NodeRetryRouted or DevWorkflowEventTypes.NodeRetryScheduled)
                  .OrderBy(static item => item.Sequence)
                  .First()
                  .EventType,
            "The decision is written before the rows it moved, so the log reads in the order a person reconstructs the round in.");
    }

    /// <summary>
    ///     The window the whole design exists to close: a failure struck after the first reset was written. Committed a
    ///     row at a time this left <c>verify</c> Pending under a <c>Succeeded</c> gate; as one transaction it leaves the
    ///     failure exactly where the dispatcher will find and re-route it.
    /// </summary>
    [Test]
    public async Task ARouteThatFailsPartWayThrough_LeavesNoResetAndNoEvent()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await SeedRoundOneAsync(store).ConfigureAwait(false);

        // The second reset names a node run this run has none of, so the cascade throws AFTER the first row was
        // written into the transaction and BEFORE the rest were.
        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() =>
                              store.RouteRetryAsync(RouteFrom(seed.RunId, Guid.NewGuid(), [VerifyId, Guid.NewGuid(), FullValidateId])))
                          .ConfigureAwait(false);

        var nodeRuns = (await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).ToDictionary(row => row.NodeKey, StringComparer.Ordinal);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRuns["verify"].Status, "The first reset must roll back with the rest: a lone Pending row is re-dispatched as if fresh.");
        AssertEx.Equal(expected: 1, nodeRuns["verify"].Attempt, "And it must not have spent an attempt on a round that never started.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRuns["integrate"].Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Failed, nodeRuns["fullvalidate"].Status, "The failure stays recorded, which is what the next dispatcher sweep re-derives the route from.");

        AssertEx.Empty((await store.ListEventsAsync(seed.RunId).ConfigureAwait(false)).Where(static item => item.EventType == DevWorkflowEventTypes.NodeRetryRouted));
    }

    /// <summary>
    ///     The idempotency the rest of this store's mutations have. A route is retried by a caller that never learned
    ///     its first attempt committed, and a second cascade would spend a second attempt on every row it touches.
    /// </summary>
    [Test]
    public async Task AReplayedRoute_AnswersTheRecordedResultAndWritesNothingAgain()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await SeedRoundOneAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        var first = await store.RouteRetryAsync(RouteFrom(seed.RunId, operationId, [VerifyId, IntegrateId, FullValidateId])).ConfigureAwait(false);
        var replay = await store.RouteRetryAsync(RouteFrom(seed.RunId, operationId, [VerifyId, IntegrateId, FullValidateId])).ConfigureAwait(false);

        AssertEx.Equal(first.Sequence, replay.Sequence, "A replay answers what the first call answered.");
        AssertEx.Equal(first.Version, replay.Version);
        AssertEx.Equal(expected: 2,
            (await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).Max(static row => row.Attempt),
            "A replayed route must not spend a second attempt on the rows it already reset.");
        AssertEx.Equal(expected: 1,
            (await store.ListEventsAsync(seed.RunId).ConfigureAwait(false)).Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryRouted));
    }

    /// <summary>
    ///     The guarantee the policy's single re-ask stands on: a route that failed leaves the SAME store usable, so
    ///     asking again on the scope that just lost is a fresh write and not a doubled one.
    ///     <para>
    ///         The first ask is failed mid-cascade, after rows and an event are already tracked, which is the only
    ///         shape where a dirty change tracker could survive. It cannot: every failure path rolls the transaction
    ///         back through one helper that clears the tracker with it, so the second ask re-reads the rows and each
    ///         reset spends exactly one attempt.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARouteAskedAgainOnTheStoreThatJustFailed_AppliesEachResetExactlyOnce()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await SeedRoundOneAsync(store).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() =>
                              store.RouteRetryAsync(RouteFrom(seed.RunId, Guid.NewGuid(), [VerifyId, Guid.NewGuid(), FullValidateId])))
                          .ConfigureAwait(false);

        // The SAME store instance, as the retry policy uses it: one scoped store, asked twice inside one tick.
        _ = await store.RouteRetryAsync(RouteFrom(seed.RunId, Guid.NewGuid(), [VerifyId, IntegrateId, FullValidateId])).ConfigureAwait(false);

        var nodeRuns = (await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).ToDictionary(row => row.NodeKey, StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     "verify",
                     "integrate",
                     "fullvalidate"
                 })
        {
            AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[key].Status);
            AssertEx.Equal(expected: 2, nodeRuns[key].Attempt, $"'{key}' spends ONE attempt across both asks; a tracker the failure left dirty would have spent two.");
        }

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryRouted), "The ask that failed left no event behind for the one that worked.");
        AssertEx.Equal(expected: 3,
            events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryScheduled),
            "One re-attempt event per reset row, not two.");
    }

    /// <summary>Where round one leaves the tail: a verification, the apply past the gate, and the full check that failed.</summary>
    private static async Task<DevWorkflowSeed> SeedRoundOneAsync(DevWorkflowStore store)
    {
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, VerifyId, "verify", seed.RunVersion).ConfigureAwait(false);
        version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, IntegrateId, "integrate", version, DevWorkflowNodeType.Tool).ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, FullValidateId, "fullvalidate", version, DevWorkflowNodeType.Tool).ConfigureAwait(false);

        foreach (var nodeRunId in new[]
                 {
                     VerifyId,
                     IntegrateId
                 })
        {
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                               nodeRunId,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Succeeded))
                           .ConfigureAwait(false);
        }

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           FullValidateId,
                           DevWorkflowVersions.Any,
                           DevWorkflowNodeRunStatus.Failed,
                           FailureClass: "ToolCommandFailed",
                           TerminalReason: "the integrated result does not build"))
                       .ConfigureAwait(false);
        return seed;
    }

    private static RouteDevWorkflowRetryCommand RouteFrom(Guid runId, Guid operationId, IReadOnlyList<Guid> resetIds) =>
        new(new AppendDevWorkflowEventCommand(runId,
                DevWorkflowVersions.Any,
                DevWorkflowEventTypes.NodeRetryRouted,
                FullValidateId,
                operationId,
                "failed",
                """{"from":"fullvalidate","to":"verify"}"""),
            [
                .. resetIds.Select(nodeRunId => new TransitionDevWorkflowNodeRunCommand(runId,
                    nodeRunId,
                    DevWorkflowVersions.Any,
                    DevWorkflowNodeRunStatus.Pending,
                    IncrementAttempt: true,
                    ClearWorkSession: true))
            ]);
}
