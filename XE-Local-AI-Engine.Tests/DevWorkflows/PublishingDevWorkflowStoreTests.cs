namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The notification seam. It wraps the store rather than living at each call site in the runtime because a missed
///     call site is a pane that silently stops updating and no test would notice — which is why the coverage assertion
///     below is the point of the design rather than decoration: a mutation added to the store interface fails this
///     file until it is announced.
/// </summary>
public sealed class PublishingDevWorkflowStoreTests
{
    private const long Sequence = 12;

    /// <summary>The retry detail a reset carries in. Unchanged on the way out is what "forwarded unenriched" means.</summary>
    private const string ReAttemptDetail = """{"attempt":2}""";

    /// <summary>
    ///     The bound the wall-clock tests measure "did not hang" against. Deliberately far wider than the collection
    ///     budget they set: this box runs several suites at once, and the claim under test is that the settle returns
    ///     at all — without the fix it waits on the collector until the test framework kills it, not until this passes.
    /// </summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    ///     The claim the decorator exists to make: nothing can commit without announcing it. Asserted against the
    ///     INTERFACE rather than a hand-picked few, so a mutation added later cannot quietly ship unannounced.
    /// </summary>
    [Test]
    public void TheProbes_CoverEveryMutationTheStoreDeclares()
    {
        var declared = typeof(IDevWorkflowStore).GetMethods()
                                                .Where(static method => method.ReturnType == typeof(Task<DevWorkflowMutationResult>))
                                                .Select(static method => method.Name)
                                                .Distinct(StringComparer.Ordinal)
                                                .OrderBy(static name => name, StringComparer.Ordinal);
        var probed = Probes().Select(static probe => probe.Method).Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal);

        AssertEx.Equal(string.Join(Environment.NewLine, declared),
            string.Join(Environment.NewLine, probed),
            "Every store mutation must be exercised below: an unannounced one is a view that silently stops updating.");
    }

    [Test]
    public async Task EveryMutation_AnnouncesItsCommitWithTheKindTheClientReactsTo()
    {
        foreach (var probe in Probes())
        {
            var (store, publisher) = Create();

            await probe.Invoke(store).ConfigureAwait(false);

            await publisher.Received(1).PublishAsync(RunId, Sequence, probe.Kind, Arg.Any<CancellationToken>());
            AssertEx.Equal(expected: 1,
                publisher.ReceivedCalls().Count(),
                $"{probe.Method} → {probe.Kind} must announce its commit exactly once, with the watermark that commit allocated.");
        }
    }

    [Test]
    public async Task AReadAnnouncesNothing()
    {
        var (store, publisher) = Create();

        _ = await store.ListNodeRunsAsync(RunId).ConfigureAwait(false);

        AssertEx.Empty(publisher.ReceivedCalls());
    }

    /// <summary>
    ///     Every mutation the store can commit, and the kind the client reacts to. A node run entering a human wait is
    ///     the one status move a client does more than repaint for, so that method carries three rows.
    /// </summary>
    private static IReadOnlyList<Probe> Probes() =>
    [
        new(nameof(IDevWorkflowStore.TransitionRunAsync),
            DevWorkflowChangeKind.Run,
            store => store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(RunId, DevWorkflowVersions.Any, DevWorkflowRunStatus.Running))),
        new(nameof(IDevWorkflowStore.AppendEventAsync),
            DevWorkflowChangeKind.Run,
            store => store.AppendEventAsync(new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeInterrupted))),
        new(nameof(IDevWorkflowStore.MaterializeNodeRunsAsync),
            DevWorkflowChangeKind.Node,
            store => store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(RunId,
                DevWorkflowVersions.Any,
                Guid.NewGuid(),
                [new DevWorkflowNodeRunSeed(NodeRunId, "research", DevWorkflowNodeType.Agent)]))),
        new(nameof(IDevWorkflowStore.TransitionNodeRunAsync),
            DevWorkflowChangeKind.Node,
            store => store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Running))),
        new(nameof(IDevWorkflowStore.TransitionNodeRunAsync),
            DevWorkflowChangeKind.Gate,
            store => store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.WaitingForApproval))),
        new(nameof(IDevWorkflowStore.TransitionNodeRunAsync),
            DevWorkflowChangeKind.Gate,
            store => store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked))),
        new(nameof(IDevWorkflowStore.RouteRetryAsync),
            DevWorkflowChangeKind.Node,
            store => store.RouteRetryAsync(new RouteDevWorkflowRetryCommand(new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, NodeRunId),
                [NodeRunTransition(DevWorkflowNodeRunStatus.Pending)]))),
        new(nameof(IDevWorkflowStore.AttachWorkSessionAsync),
            DevWorkflowChangeKind.Node,
            store => store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(RunId, NodeRunId, DevWorkflowVersions.Any, Guid.NewGuid()))),
        new(nameof(IDevWorkflowStore.AppendArtifactAsync),
            DevWorkflowChangeKind.Artifact,
            store => store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(RunId,
                Guid.NewGuid(),
                NodeRunId,
                DevWorkflowVersions.Any,
                Guid.NewGuid(),
                DevWorkflowArtifactKind.Plan,
                "plan.md",
                "text/markdown",
                "sha",
                SizeBytes: 4,
                "reference"))),
        new(nameof(IDevWorkflowStore.RecordArtifactUsesAsync),
            DevWorkflowChangeKind.Artifact,
            store => store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand(RunId,
                NodeRunId,
                DevWorkflowVersions.Any,
                Guid.NewGuid(),
                [Guid.NewGuid()]))),
        new(nameof(IDevWorkflowStore.MarkDependentsStaleAsync),
            DevWorkflowChangeKind.Artifact,
            store => store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand(RunId, Guid.NewGuid(), Guid.NewGuid(), DevWorkflowVersions.Any))),
        new(nameof(IDevWorkflowStore.RecordDecisionAsync),
            DevWorkflowChangeKind.Gate,
            store => store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(RunId,
                Guid.NewGuid(),
                NodeRunId,
                DevWorkflowVersions.Any,
                Guid.NewGuid(),
                DevWorkflowDecisionKind.Approve)))
    ];

    /// <summary>
    ///     One collection budget for the WHOLE retry route, however many resets it carries. The route's resets come from
    ///     the retry target's descendants, so they are bounded only by the graph's width: a deadline per reset would let
    ///     a stalled collector hold the dispatcher tick for the width times the budget, and the fix loop's re-send after
    ///     a lost concurrency race would double it again.
    ///     <para>
    ///         Proved on the tokens rather than on the clock: two <c>CancellationToken</c>s are equal when they came from
    ///         the same source, so ten resets seeing ONE distinct deadline is the claim itself.
    ///     </para>
    ///     <para>
    ///         Both halves of what "one budget" means are asserted here, and the second half is a REVERSAL of the
    ///         earlier reading that every reset is offered no matter what. Every reset is offered <b>while the budget
    ///         lasts</b>; once it is spent, nothing further is even scheduled — an answer that arrives after the route
    ///         has committed cannot be used, so starting it would only pile work up behind a boundary nobody is
    ///         watching.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARetryRoute_BoundsEveryResetWithOneDeadline()
    {
        const int resets = 10;
        var route = new RouteDevWorkflowRetryCommand(new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, NodeRunId),
            [.. Enumerable.Range(0, resets).Select(static _ => ReAttempt())]);

        // While the budget lasts. A budget nothing can exhaust, because this arm is about the token's identity rather
        // than the clock — a wall-clock budget here would only make the assertion flake on a loaded box.
        var offered = new StubDevWorkflowNodeTelemetrySource
        {
            ExpectedEntries = resets
        };
        var (store, publisher) = Create(offered, Hang, collectionSlots: resets);

        _ = await store.RouteRetryAsync(route).ConfigureAwait(false);

        // Offering is EVENTUAL: the decorator hands each collection to the thread pool so a collector that blocks
        // before its first await cannot hold the route, which means the tenth reset may not have reached the collector
        // yet at the instant the route commits.
        await offered.AllEntered.WaitAsync(Hang).ConfigureAwait(false);
        AssertEx.Equal(resets, offered.Calls, "Every reset is offered while the budget lasts; only the budget is shared.");
        AssertEx.Equal(expected: 1,
            offered.Deadlines.Distinct().Count(),
            "All ten collections must run under ONE deadline, or the route costs the graph's width times the budget.");
        await publisher.Received(1).PublishAsync(RunId, Sequence, DevWorkflowChangeKind.Node, Arg.Any<CancellationToken>());

        // And once it is spent, nothing more is scheduled. The first reset's collector stalls for the whole 300 ms, so
        // the shared deadline is gone by the time the second is reached and the nine behind it start no collection at
        // all — which is also why the route still returns inside its budget.
        var stalled = new StubDevWorkflowNodeTelemetrySource
        {
            Delay = TimeSpan.FromMinutes(1),
            ExpectedEntries = 1
        };
        var budget = TimeSpan.FromMilliseconds(300);
        var (spent, _) = Create(stalled, budget, collectionSlots: resets);

        var elapsed = Stopwatch.StartNew();
        _ = await spent.RouteRetryAsync(route).ConfigureAwait(false);
        elapsed.Stop();

        await stalled.AllEntered.WaitAsync(Hang).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, stalled.Calls, "A reset reached after the shared budget expired must not schedule a collection at all.");

        // A generous bound, for the same reason the gated tests below use one: the collector stalls for a MINUTE, so
        // any bounded return is the claim, and a multiple of the 300 ms budget only measures how loaded the box is.
        AssertEx.True(elapsed.Elapsed < Hang,
            $"A route of {resets} resets behind a stalled collector took {elapsed.ElapsedMilliseconds} ms against a {budget.TotalMilliseconds} ms budget.");
    }

    /// <summary>
    ///     The ceiling BEHIND the deadline. The deadline abandons the caller's wait, not the collection, so without a
    ///     bound on the collections themselves a collector that never terminates costs a thread-pool worker and a
    ///     service scope per settle, for the life of the process — and a wide retry route multiplies that by the graph's
    ///     width. One slot here, so one stuck collector is the whole pool.
    ///     <para>
    ///         The release is the other half: a slot comes back when the COLLECTOR terminates, not when the settle
    ///         stopped waiting for it. Releasing it on the abandoned wait would let the next settle start work beside
    ///         the stuck one, which is the accumulation the pool exists to stop.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASettle_ThatFindsTheCollectionPoolFull_GoesAheadWithoutOne()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromMilliseconds(300);
        var telemetry = new StubDevWorkflowNodeTelemetrySource
        {
            IgnoresCancellationUntil = gate,
            Answer = new DevWorkflowNodeTelemetry(InputTokens: 5),
            ExpectedEntries = 1
        };
        var harness = CreateHarness(telemetry, budget, collectionSlots: 1);

        // The first settle takes the only slot, is abandoned at the deadline, and is still holding it.
        _ = await harness.Store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);
        await telemetry.AllEntered.WaitAsync(Hang).ConfigureAwait(false);

        var elapsed = Stopwatch.StartNew();
        _ = await harness.Store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);
        elapsed.Stop();

        AssertEx.Equal(expected: 1, telemetry.Calls, "A settle that finds every slot in use must not start a second collection.");
        AssertEx.Equal(expected: 1, harness.Scopes.Created, "And it opens no scope: admission is checked before the work is scheduled.");
        // A tight bound is safe HERE, unlike the route test above: a refused settle starts no collection, arms no timer
        // and awaits nothing, so it cannot be slowed by a loaded thread pool the way a 300 ms deadline can.
        AssertEx.True(elapsed.Elapsed < budget * 3,
            $"The refused settle waited {elapsed.ElapsedMilliseconds} ms against a {budget.TotalMilliseconds} ms budget; it should not wait at all.");
        _ = await harness.Inner.Received(2)
                         .TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static forwarded => forwarded.Telemetry == null),
                             Arg.Any<CancellationToken>());

        // Re-offered in a loop rather than asserted once: the release runs in the abandoned task's own finally, just
        // after the scope it disposes, so the slot comes back a moment after the gate opens rather than with it.
        gate.SetResult();
        telemetry.IgnoresCancellationUntil = null;
        var giveUpAt = DateTimeOffset.UtcNow + Hang;
        while (telemetry.Calls < 2 && DateTimeOffset.UtcNow < giveUpAt)
        {
            _ = await harness.Store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);
            if (telemetry.Calls < 2)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        AssertEx.Equal(expected: 2, telemetry.Calls, "The stuck collector's slot comes back when it finally returns, and the next settle is measured again.");
    }

    /// <summary>The other half of the same claim: a single settle owns its own budget and shares nothing with the next one.</summary>
    [Test]
    public async Task TwoSettles_EachGetTheirOwnDeadline()
    {
        var telemetry = new StubDevWorkflowNodeTelemetrySource();
        var (store, _) = Create(telemetry, TimeSpan.FromSeconds(5));

        _ = await store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, telemetry.Deadlines.Distinct().Count(), "One settle, one budget — a slow settle may not shorten the next one's.");
    }

    /// <summary>
    ///     The deadline is a WALL CLOCK, not a request. This collector never observes its token — it waits on a gate
    ///     only the test opens — so a decorator that merely called <c>CancelAfter</c> and awaited would hold the
    ///     terminal transition open forever and the node run would never settle.
    ///     <para>
    ///         The three claims are asserted together because they are one design: the settle goes ahead inside the
    ///         budget, the ORIGINAL command is what reaches the inner store, and the abandoned collection is still
    ///         holding a scope of its OWN — so it is not reading on the <c>DbContext</c> the mutation writes on.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASettle_WhoseCollectorIgnoresTheDeadline_GoesAheadWithTheOriginalCommand()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromMilliseconds(300);
        var telemetry = new StubDevWorkflowNodeTelemetrySource
        {
            IgnoresCancellationUntil = gate,
            Answer = new DevWorkflowNodeTelemetry(InputTokens: 5)
        };
        var harness = CreateHarness(telemetry, budget);

        var elapsed = Stopwatch.StartNew();
        _ = await harness.Store.TransitionNodeRunAsync(NodeRunTransition(DevWorkflowNodeRunStatus.Blocked)).ConfigureAwait(false);
        elapsed.Stop();

        // A generous bound on purpose: nothing but the test opens that gate, so ANY bounded completion is the claim.
        // Before this change the settle waited on the collector forever and only the test framework's own timeout ended it.
        AssertEx.True(elapsed.Elapsed < Hang,
            $"A collector that ignores cancellation held the settle for {elapsed.ElapsedMilliseconds} ms against a {budget.TotalMilliseconds} ms budget.");
        _ = await harness.Inner.Received(1)
                         .TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static forwarded => forwarded.Telemetry == null),
                             Arg.Any<CancellationToken>());
        await AssertEx.EventuallyAsync(() => harness.Scopes.Created == 1,
                          Hang,
                          "The collection reads on a scope it owns, not on the one the mutation is about to write through.")
                      .ConfigureAwait(false);
        AssertEx.Equal(expected: 0, harness.Scopes.Disposed, "And it still holds it: the settle abandoned the wait, not the collection's resources.");

        // The late arm: the collection finishes long after the transition it would have enriched, and writes nothing.
        var callsBefore = harness.Inner.ReceivedCalls().Count();
        gate.SetResult();
        await telemetry.Finished.ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => harness.Scopes.Disposed == 1, Hang, "The abandoned collection disposes its own scope when it finally lands.")
                      .ConfigureAwait(false);
        AssertEx.Equal(callsBefore, harness.Inner.ReceivedCalls().Count(), "A late collection is dropped — it must not reach the store at all.");
    }

    /// <summary>The same wall clock on the other write path: a retry route's resets forward unenriched rather than hanging.</summary>
    [Test]
    public async Task ARetryRoute_WhoseCollectorIgnoresTheDeadline_ForwardsItsResetsUnenriched()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromMilliseconds(300);
        var telemetry = new StubDevWorkflowNodeTelemetrySource
        {
            IgnoresCancellationUntil = gate,
            Answer = new DevWorkflowNodeTelemetry(InputTokens: 5)
        };
        var harness = CreateHarness(telemetry, budget);
        var route = new RouteDevWorkflowRetryCommand(new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, NodeRunId),
            [ReAttempt()]);

        var elapsed = Stopwatch.StartNew();
        _ = await harness.Store.RouteRetryAsync(route).ConfigureAwait(false);
        elapsed.Stop();

        AssertEx.True(elapsed.Elapsed < Hang,
            $"The route waited {elapsed.ElapsedMilliseconds} ms on a collector that ignores cancellation, against a {budget.TotalMilliseconds} ms budget.");
        _ = await harness.Inner.Received(1)
                         .RouteRetryAsync(Arg.Is<RouteDevWorkflowRetryCommand>(static forwarded => forwarded.Resets[0].DetailJson == ReAttemptDetail),
                             Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 0, harness.Scopes.Disposed, "The abandoned collection keeps its own scope while the route commits.");

        var callsBefore = harness.Inner.ReceivedCalls().Count();
        gate.SetResult();
        await telemetry.Finished.ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => harness.Scopes.Disposed == 1, Hang, "It gives the scope back once it lands.").ConfigureAwait(false);
        AssertEx.Equal(callsBefore, harness.Inner.ReceivedCalls().Count(), "And its answer reaches nothing.");
    }

    private static TransitionDevWorkflowNodeRunCommand NodeRunTransition(DevWorkflowNodeRunStatus target) =>
        new(RunId, NodeRunId, DevWorkflowVersions.Any, target);

    /// <summary>The shape both re-attempt write paths build: a Pending reset that spends an attempt and carries a detail to merge into.</summary>
    private static TransitionDevWorkflowNodeRunCommand ReAttempt() =>
        new(RunId,
            NodeRunId,
            DevWorkflowVersions.Any,
            DevWorkflowNodeRunStatus.Pending,
            DetailJson: ReAttemptDetail,
            IncrementAttempt: true);

    private static (IDevWorkflowStore Store, IDevWorkflowEventPublisher Publisher) Create(StubDevWorkflowNodeTelemetrySource? source = null,
        TimeSpan? collectionTimeout = null,
        int collectionSlots = 4)
    {
        var harness = CreateHarness(source, collectionTimeout, collectionSlots);
        return (harness.Store, harness.Publisher);
    }

    /// <summary>
    ///     Every store here gets its OWN admission pool. The production one is static and process-wide — it has to be,
    ///     since the decorator is registered scoped — so a suite sharing it would let one test's stuck collector starve
    ///     another's, in whichever order the runner happened to pick.
    /// </summary>
    private static Harness CreateHarness(StubDevWorkflowNodeTelemetrySource? source = null,
        TimeSpan? collectionTimeout = null,
        int collectionSlots = 4)
    {
        var inner = Substitute.For<IDevWorkflowStore>();
        var result = new DevWorkflowMutationResult(RunId, Sequence, Version: 2, DevWorkflowRunStatus.Running, GraphRevision: 0);
        inner.TransitionRunAsync(Arg.Any<TransitionDevWorkflowRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AppendEventAsync(Arg.Any<AppendDevWorkflowEventCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.MaterializeNodeRunsAsync(Arg.Any<MaterializeDevWorkflowNodesCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AttachWorkSessionAsync(Arg.Any<AttachDevWorkflowWorkSessionCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AppendArtifactAsync(Arg.Any<AppendDevWorkflowArtifactCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.RecordArtifactUsesAsync(Arg.Any<RecordDevWorkflowArtifactUsesCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.MarkDependentsStaleAsync(Arg.Any<MarkDevWorkflowStaleCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.RecordDecisionAsync(Arg.Any<RecordDevWorkflowDecisionCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.ListNodeRunsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        var publisher = Substitute.For<IDevWorkflowEventPublisher>();

        // A telemetry source that answers nothing: this suite is about the announcement, and a collector that returned
        // something would only add a second read to every probe.
        var telemetry = source ?? new StubDevWorkflowNodeTelemetrySource();
        var scopes = new RecordingTelemetryScopeFactory(inner, telemetry);
        return new Harness(new PublishingDevWorkflowStore(inner,
                publisher,
                scopes,
                new DevWorkflowGraphCache(),
                new DevWorkflowNodeTelemetryCollectionPool(collectionSlots),
                NullLogger<PublishingDevWorkflowStore>.Instance,
                collectionTimeout),
            publisher,
            inner,
            scopes);
    }

    private sealed record Probe(string Method, DevWorkflowChangeKind Kind, Func<IDevWorkflowStore, Task> Invoke);

    private sealed record Harness(
        IDevWorkflowStore Store,
        IDevWorkflowEventPublisher Publisher,
        IDevWorkflowStore Inner,
        RecordingTelemetryScopeFactory Scopes);
}
