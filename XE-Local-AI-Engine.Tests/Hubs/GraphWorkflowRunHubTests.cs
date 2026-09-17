namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The subscription side of the graph-workflow run hub. The hub reads its replay through
///     <see cref="IGraphWorkflowRunService.ListEventsAsync" /> — the same paged member the event endpoint answers
///     with — so every case below drives the REAL run service over a substituted store rather than substituting the
///     service: what these assertions are about is the page the subscriber is handed, and a stubbed page would be the
///     test asserting its own arithmetic.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowRunHubTests
{
    /// <summary>The replay window the hub is configured with below. Read from the OPTION, unlike the Dev hub's private const.</summary>
    private const int ReplayLimit = 5;

    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task SubscribeRun_JoinsTheGroupBeforeReadingTheReplay()
    {
        var store = Store();
        using var fixture = CreateHub(store);

        _ = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        // The other order leaves a window in which a change published between the read and the join reaches nobody.
        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync("connection", $"graph-workflow-run-{RunId:N}", Arg.Any<CancellationToken>());
            store.ListEventsAsync(RunId, 0, ReplayLimit + 1, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SubscribeRun_ReturnsTheRunStateItsCountersAndTheEventsAfterTheWatermark()
    {
        using var fixture = CreateHub(Store([Event(8), Event(9)]));

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 7).ConfigureAwait(false);

        AssertEx.Equal(RunId, snapshot.RunId);
        AssertEx.Equal("Running", snapshot.Status);
        AssertEx.Equal(expected: 1, snapshot.QueuedNodeCount);
        AssertEx.Equal(expected: 1, snapshot.RunningNodeCount);
        AssertEx.Equal(expected: 0, snapshot.PendingDecisionCount, "S1 writes no human wait: the counter is carried for S2 and reads zero until then.");
        AssertEx.Equal(expected: 9L, snapshot.LastSeq);
        AssertEx.Equal(expected: 2, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    /// <summary>
    ///     The watermark is the last row the subscriber was actually handed, whichever side of the run's own sequence
    ///     it falls. Here it is past it: the run row is read before the group join and before the replay page, so an
    ///     event committed in between is delivered by this snapshot and the cursor has to say so.
    /// </summary>
    [Test]
    public async Task SubscribeRun_WhenTheReplayOutrunsTheRunItWasReadFrom_ReportsTheDeliveredWatermark()
    {
        using var fixture = CreateHub(Store([Event(10), Event(12)]));

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(expected: 12L, snapshot.LastSeq, "the run row read 9; the page delivered 12, and that is what the client has seen.");
    }

    /// <summary>An empty page moves the subscriber nowhere: it has seen nothing new, so its own watermark stands.</summary>
    [Test]
    public async Task SubscribeRun_WithNothingAfterTheWatermark_KeepsTheCallersOwnWatermark()
    {
        using var fixture = CreateHub(Store());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 4).ConfigureAwait(false);

        AssertEx.Empty(snapshot.Events);
        AssertEx.Equal(expected: 4L, snapshot.LastSeq, "the run row read 9, but nothing between 4 and 9 was delivered for the client to skip past.");
    }

    [Test]
    public async Task SubscribeRun_AtTheReplayCap_IsNotTruncated()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayLimit).Select(sequence => Event(sequence))]));

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeRun_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayLimit + 1).Select(sequence => Event(sequence))]));

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Events.Count);
        AssertEx.True(snapshot.ReplayTruncated, "the cap is observed one row over it, never inferred from a full page.");
        AssertEx.Equal((long)ReplayLimit,
            snapshot.LastSeq,
            "the cursor is the last row DELIVERED, not the run's own sequence: taking that would skip every event the cap cut off.");
    }

    /// <summary>
    ///     What the cursor is FOR: a truncated snapshot has to be resumable, and the events the cap cut off have to be
    ///     reachable from the watermark it handed back. Nothing replays them a second time, so a cursor past them would
    ///     lose them for good.
    /// </summary>
    [Test]
    public async Task SubscribeRun_ResumedFromItsOwnCursor_DeliversExactlyTheEventsTheCapCutOff()
    {
        using var fixture = CreateHub(PagingStore([.. Enumerable.Range(1, 7).Select(sequence => Event(sequence))]));

        var first = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);
        AssertEx.True(first.ReplayTruncated);
        AssertEx.Equal(expected: 5L, first.LastSeq);

        var second = await fixture.Hub.SubscribeRun(RunId, first.LastSeq).ConfigureAwait(false);
        AssertEx.False(second.ReplayTruncated, "two rows are left and the cap is five.");
        AssertEx.Equal(expected: 2, second.Events.Count, "exactly the rest, with nothing repeated.");
        AssertEx.Equal(expected: 6L, second.Events[0].Seq, "and in order, starting one past the cursor.");
        AssertEx.Equal(expected: 7L, second.Events[1].Seq);
        AssertEx.Equal(expected: 7L, second.LastSeq);

        var third = await fixture.Hub.SubscribeRun(RunId, second.LastSeq).ConfigureAwait(false);
        AssertEx.Empty(third.Events);
        AssertEx.Equal(second.LastSeq, third.LastSeq, "a caught-up subscriber keeps the watermark it came with.");
    }

    [Test]
    public async Task SubscribeRun_WhenTheRunIsUnknown_ThrowsWithoutJoiningAGroup()
    {
        var store = Store();
        store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new GraphWorkflowNotFoundException("gone"));
        using var fixture = CreateHub(store);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WithAnEmptyRunId_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(Guid.Empty, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WithANegativeWatermark_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: -1)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WhenTheFeatureIsDisabled_ThrowsWithoutReachingTheRuntime()
    {
        var store = Store();
        using var fixture = CreateHub(store, enabled: false);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Empty(store.ReceivedCalls());
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task UnsubscribeRun_LeavesTheRunGroup()
    {
        using var fixture = CreateHub(Store());

        await fixture.Hub.UnsubscribeRun(RunId).ConfigureAwait(false);

        await fixture.Groups.Received(1).RemoveFromGroupAsync("connection", $"graph-workflow-run-{RunId:N}", Arg.Any<CancellationToken>());
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(GraphWorkflowRunHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    private static IGraphWorkflowStore Store(IReadOnlyList<GraphWorkflowRunEventSnapshot>? events = null)
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListEventsAsync(RunId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(events ?? []);
        return WithRun(store);
    }

    /// <summary>The run row and its node runs — the two reads the run service composes its detail from.</summary>
    private static IGraphWorkflowStore WithRun(IGraphWorkflowStore store)
    {
        var run = new GraphWorkflowRunSnapshot(RunId,
            RequestId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            "graph-hash",
            GraphWorkflowRunStatus.Running,
            GraphWorkflowFailureClass.None,
            """{"schemaVersion":1,"nodes":[],"edges":[]}""",
            InputJson: null,
            OutputJson: null,
            Seq: 9,
            Version: 6,
            CancelRequestedAtUtc: null,
            StartedAtUtc: 11,
            CompletedAtUtc: null,
            CreatedAtUtc: 10);
        store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(run);
        store.ListNodeRunsAsync(RunId, Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<GraphWorkflowNodeRunSnapshot>>(
             [
                 NodeRun("draft", GraphWorkflowNodeRunStatus.Running),
                 NodeRun("review", GraphWorkflowNodeRunStatus.Queued),
                 NodeRun("finish", GraphWorkflowNodeRunStatus.Pending)
             ]);
        return store;
    }

    /// <summary>
    ///     A store that actually PAGES: it honours the watermark and the limit it is called with, which is what a test
    ///     about resuming from a cursor needs — one that answers the same rows whatever it is asked cannot see a gap.
    /// </summary>
    private static IGraphWorkflowStore PagingStore(IReadOnlyList<GraphWorkflowRunEventSnapshot> all)
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        _ = WithRun(store);
        store.ListEventsAsync(RunId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 IReadOnlyList<GraphWorkflowRunEventSnapshot> page =
                     [.. all.Where(@event => @event.Seq > call.ArgAt<long>(1)).Take(call.ArgAt<int>(2))];
                 return page;
             });
        return store;
    }

    private static GraphWorkflowNodeRunSnapshot NodeRun(string nodeKey, GraphWorkflowNodeRunStatus status) =>
        new(Guid.NewGuid(),
            RunId,
            nodeKey,
            GraphWorkflowNodeKind.Agent,
            status,
            Attempt: 1,
            PendingDecisionKind: null,
            DecisionOperationId: null,
            DecidedBySubject: null,
            GraphWorkflowFailureClass.None,
            Error: null,
            InputJson: null,
            OutputJson: null,
            InvocationId: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            UpdatedAtUtc: 20);

    private static GraphWorkflowRunEventSnapshot Event(long sequence) =>
        new(Guid.NewGuid(), RunId, sequence, "node.started", NodeKey: "draft", DetailJson: null, CreatedAtUtc: 100);

    private static HubFixture CreateHub(IGraphWorkflowStore store, bool enabled = true)
    {
        var options = Options.Create(new GraphWorkflowOptions
        {
            Enabled = enabled,
            EventReplayLimit = ReplayLimit
        });

        // The REAL run service over the substituted store. The replay window, the one-over-the-cap probe and the
        // delivered watermark are its arithmetic and the hub only reports the page it is handed, so substituting the
        // service would leave the cases below asserting numbers the test itself computed.
        // The dispatcher signal is the repo's own recording double rather than a substitute: its interface is
        // internal, which NSubstitute cannot proxy, and nothing a subscription does signals the dispatcher anyway.
        return CreateHub(new GraphWorkflowRunService(store,
                new RecordingGraphWorkflowDispatcherSignal(),
                Substitute.For<IToolInvocationService>(),
                options),
            options);
    }

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IGraphWorkflowRunService runs, IOptions<GraphWorkflowOptions> options)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new GraphWorkflowRunHub(runs, options)
        {
            Context = context,
            Groups = groups,
            Clients = clients
        };
        return new HubFixture(hub, groups);
    }

    private sealed record HubFixture(GraphWorkflowRunHub Hub, IGroupManager Groups) : IDisposable
    {
        public void Dispose() =>
            Hub.Dispose();
    }
}
