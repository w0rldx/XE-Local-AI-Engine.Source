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
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GraphWorkflowRunHubTests
{
    /// <summary>The replay window the hub is configured with below. Read from the OPTION, unlike the Dev hub's private const.</summary>
    private const int ReplayLimit = 5;

    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task SubscribeRun_JoinsTheGroupBeforeReadingTheReplay()
    {
        var store = Store();
        using var fixture = CreateHub(store, Runs());

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
        using var fixture = CreateHub(Store([Event(8), Event(9)]), Runs());

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
    ///     The watermark is the highest row the subscriber was actually handed. The run's own sequence is read before
    ///     the group join and before the replay page, so a change committed in between leaves it behind the events this
    ///     snapshot carries — and a client resuming from it would skip them.
    /// </summary>
    [Test]
    public async Task SubscribeRun_WhenTheReplayOutrunsTheRunItWasReadFrom_ReportsTheHigherWatermark()
    {
        using var fixture = CreateHub(Store([Event(10), Event(12)]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(expected: 12L, snapshot.LastSeq, "the run row read 9; the page delivered 12, and that is what the client has seen.");
    }

    [Test]
    public async Task SubscribeRun_AtTheReplayCap_IsNotTruncated()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayLimit).Select(sequence => Event(sequence))]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeRun_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayLimit + 1).Select(sequence => Event(sequence))]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Events.Count);
        AssertEx.True(snapshot.ReplayTruncated, "the cap is observed one row over it, never inferred from a full page.");
    }

    [Test]
    public async Task SubscribeRun_WhenTheRunIsUnknown_ThrowsWithoutJoiningAGroup()
    {
        var runs = Substitute.For<IGraphWorkflowRunService>();
        runs.GetRunAsync(RunId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new GraphWorkflowNotFoundException("gone"));
        using var fixture = CreateHub(Store(), runs);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WithAnEmptyRunId_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store(), Runs());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(Guid.Empty, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WithANegativeWatermark_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store(), Runs());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: -1)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeRun_WhenTheFeatureIsDisabled_ThrowsWithoutReachingTheRuntime()
    {
        var runs = Runs();
        using var fixture = CreateHub(Store(), runs, enabled: false);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeRun(RunId, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Empty(runs.ReceivedCalls());
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task UnsubscribeRun_LeavesTheRunGroup()
    {
        using var fixture = CreateHub(Store(), Runs());

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
        return store;
    }

    private static IGraphWorkflowRunService Runs()
    {
        var runs = Substitute.For<IGraphWorkflowRunService>();
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
        runs.GetRunAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new GraphWorkflowRunDetail(run,
            [
                NodeRun("draft", GraphWorkflowNodeRunStatus.Running),
                NodeRun("review", GraphWorkflowNodeRunStatus.Queued),
                NodeRun("finish", GraphWorkflowNodeRunStatus.Pending)
            ]));
        return runs;
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

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IGraphWorkflowStore store, IGraphWorkflowRunService runs, bool enabled = true)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new GraphWorkflowRunHub(store,
            runs,
            Options.Create(new GraphWorkflowOptions
            {
                Enabled = enabled,
                EventReplayLimit = ReplayLimit
            }))
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
