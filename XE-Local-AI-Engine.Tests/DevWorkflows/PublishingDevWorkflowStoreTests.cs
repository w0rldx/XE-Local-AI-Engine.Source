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
            store => store.RouteRetryAsync(new RouteDevWorkflowRetryCommand(
                new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, NodeRunId),
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
    ///         the same source, so ten resets seeing ONE distinct deadline is the claim itself. The elapsed bound is the
    ///         same claim in wall-clock terms, with a margin wide enough not to flake.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARetryRoute_BoundsEveryResetWithOneDeadline()
    {
        const int resets = 10;
        var budget = TimeSpan.FromMilliseconds(300);
        var telemetry = new StubDevWorkflowNodeTelemetrySource { Delay = TimeSpan.FromMinutes(1) };
        var (store, publisher) = Create(telemetry, budget);
        var route = new RouteDevWorkflowRetryCommand(
            new AppendDevWorkflowEventCommand(RunId, DevWorkflowVersions.Any, DevWorkflowEventTypes.NodeRetryRouted, NodeRunId),
            [.. Enumerable.Range(0, resets).Select(static _ => ReAttempt())]);

        var elapsed = Stopwatch.StartNew();
        _ = await store.RouteRetryAsync(route).ConfigureAwait(false);
        elapsed.Stop();

        AssertEx.Equal(resets, telemetry.Calls, "Every reset is still offered to the collector; only the budget is shared.");
        AssertEx.Equal(expected: 1,
            telemetry.Deadlines.Distinct().Count(),
            "All ten collections must run under ONE deadline, or the route costs the graph's width times the budget.");
        AssertEx.True(elapsed.Elapsed < budget * 3,
            $"A route of {resets} stalled collections took {elapsed.ElapsedMilliseconds} ms against a {budget.TotalMilliseconds} ms budget.");
        await publisher.Received(1).PublishAsync(RunId, Sequence, DevWorkflowChangeKind.Node, Arg.Any<CancellationToken>());
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

    private static TransitionDevWorkflowNodeRunCommand NodeRunTransition(DevWorkflowNodeRunStatus target) =>
        new(RunId, NodeRunId, DevWorkflowVersions.Any, target);

    /// <summary>The shape both re-attempt write paths build: a Pending reset that spends an attempt and carries a detail to merge into.</summary>
    private static TransitionDevWorkflowNodeRunCommand ReAttempt() =>
        new(RunId,
            NodeRunId,
            DevWorkflowVersions.Any,
            DevWorkflowNodeRunStatus.Pending,
            DetailJson: """{"attempt":2}""",
            IncrementAttempt: true);

    private static (IDevWorkflowStore Store, IDevWorkflowEventPublisher Publisher) Create(StubDevWorkflowNodeTelemetrySource? source = null,
        TimeSpan? collectionTimeout = null)
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
        return (new PublishingDevWorkflowStore(inner,
            publisher,
            telemetry,
            new DevWorkflowGraphCache(),
            NullLogger<PublishingDevWorkflowStore>.Instance,
            collectionTimeout), publisher);
    }

    private sealed record Probe(string Method, DevWorkflowChangeKind Kind, Func<IDevWorkflowStore, Task> Invoke);
}
