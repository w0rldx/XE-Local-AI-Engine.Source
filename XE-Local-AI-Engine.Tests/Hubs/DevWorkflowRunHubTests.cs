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
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevWorkflowRunHubTests
{
    private const int ReplayCap = 200;

    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid GateNodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Test]
    public async Task SubscribeRun_JoinsTheGroupBeforeReadingTheReplay()
    {
        var store = Store();
        using var fixture = CreateHub(store, Runs());

        _ = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        // The other order leaves a window in which a change published between the read and the join reaches nobody.
        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync("connection", $"dev-workflow-run-{RunId:N}", Arg.Any<CancellationToken>());
            store.ListEventsAsync(RunId, 0, ReplayCap + 1, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SubscribeRun_ReturnsTheRunStateItsCountersAndTheEventsAfterTheWatermark()
    {
        using var fixture = CreateHub(Store([Event(8), Event(9)]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 7).ConfigureAwait(false);

        AssertEx.Equal(RunId, snapshot.RunId);
        AssertEx.Equal("WaitingForApproval", snapshot.Status);
        AssertEx.Equal(expected: 1, snapshot.RunningNodeCount);
        AssertEx.Equal(expected: 0, snapshot.QueuedNodeCount);
        AssertEx.Equal(expected: 1, snapshot.PendingDecisionCount);
        AssertEx.Equal(GateNodeRunId, snapshot.BlockingGateNodeRunId!.Value);
        AssertEx.Equal(expected: 14L, snapshot.LastSeq);
        AssertEx.Equal(expected: 2, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeRun_AtTheReplayCap_IsNotTruncated()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayCap).Select(sequence => Event(sequence))]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeRun_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayCap + 1).Select(sequence => Event(sequence))]), Runs());

        var snapshot = await fixture.Hub.SubscribeRun(RunId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.True(snapshot.ReplayTruncated, "the cap is observed one row over it, never inferred from a full page.");
    }

    [Test]
    public async Task SubscribeRun_WhenTheRunIsUnknown_ThrowsWithoutJoiningAGroup()
    {
        var runs = Substitute.For<IDevWorkflowRunService>();
        runs.GetAsync(RunId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new DevWorkflowNotFoundException("gone"));
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

        await fixture.Groups.Received(1).RemoveFromGroupAsync("connection", $"dev-workflow-run-{RunId:N}", Arg.Any<CancellationToken>());
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(DevWorkflowRunHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    private static IDevWorkflowStore Store(IReadOnlyList<DevWorkflowRunEventSnapshot>? events = null)
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.ListEventsAsync(RunId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(events ?? []);
        return store;
    }

    private static IDevWorkflowRunService Runs()
    {
        var runs = Substitute.For<IDevWorkflowRunService>();
        var run = new DevWorkflowRunSnapshot(RunId,
            WorkItemId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            "graph-hash",
            """{"schemaVersion":1,"nodes":[{"nodeKey":"approval","nodeType":"HumanGate"}],"edges":[]}""",
            GraphRevision: 0,
            DevWorkflowRunStatus.WaitingForApproval,
            LastSequence: 14,
            FailureClass: null,
            TerminalReason: null,
            StartedAtUtc: 11,
            EndedAtUtc: null,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 20,
            Version: 6);
        runs.GetAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new DevWorkflowRunDetail(run, [NodeRun(GateNodeRunId, DevWorkflowNodeRunStatus.WaitingForApproval), NodeRun(Guid.NewGuid(), DevWorkflowNodeRunStatus.Running)], 1, GateNodeRunId));
        return runs;
    }

    private static DevWorkflowNodeRunSnapshot NodeRun(Guid id, DevWorkflowNodeRunStatus status) =>
        new(id,
            RunId,
            status == DevWorkflowNodeRunStatus.WaitingForApproval ? "approval" : "research",
            DevWorkflowNodeType.HumanGate,
            Attempt: 1,
            MaxAttempts: 1,
            SessionResumes: 0,
            status,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 5,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: null,
            EndedAtUtc: null,
            CreatedAtUtc: 10);

    private static DevWorkflowRunEventSnapshot Event(long sequence) =>
        new(Guid.NewGuid(), RunId, NodeRunId: null, sequence, "node.started", DetailJson: null, OperationId: null, Outcome: null, OccurredAtUtc: 100);

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IDevWorkflowStore store, IDevWorkflowRunService runs, bool enabled = true)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new DevWorkflowRunHub(store,
            runs,
            Options.Create(new DevWorkflowOptions
            {
                Enabled = enabled
            }))
        {
            Context = context,
            Groups = groups,
            Clients = clients
        };
        return new HubFixture(hub, groups);
    }

    private sealed record HubFixture(DevWorkflowRunHub Hub, IGroupManager Groups) : IDisposable
    {
        public void Dispose() =>
            Hub.Dispose();
    }
}
