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
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkSessionHubTests
{
    private const int ReplayCap = 200;

    private static readonly Guid SessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid CurrentTaskId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Test]
    public async Task SubscribeSession_JoinsTheGroupBeforeReadingTheReplay()
    {
        var service = Service();
        using var fixture = CreateHub(service);

        _ = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        // The other order leaves a window in which a change published between the read and the join reaches nobody.
        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync("connection", $"work-session-{SessionId:N}", Arg.Any<CancellationToken>());
            service.ListEventsAsync(SessionId, 0, ReplayCap + 1, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SubscribeSession_ReturnsTheSessionStateAndTheEventsAfterTheWatermark()
    {
        var service = Service(events: [Event(sequence: 8), Event(sequence: 9)]);
        using var fixture = CreateHub(service);

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 7).ConfigureAwait(false);

        AssertEx.Equal(SessionId, snapshot.SessionId);
        AssertEx.Equal("Running", snapshot.Status);
        AssertEx.Equal(3, snapshot.Step);
        AssertEx.Equal(CurrentTaskId, snapshot.CurrentTaskId!.Value);
        AssertEx.Equal(9L, snapshot.LastSeq);
        AssertEx.Equal(2, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
        await service.Received(1).ListEventsAsync(SessionId, 7, ReplayCap + 1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscribeSession_AtTheReplayCap_IsNotTruncated()
    {
        var service = Service(events: [.. Enumerable.Range(1, ReplayCap).Select(sequence => Event(sequence))]);
        using var fixture = CreateHub(service);

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeSession_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        var service = Service(events: [.. Enumerable.Range(1, ReplayCap + 1).Select(sequence => Event(sequence))]);
        using var fixture = CreateHub(service);

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.True(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeSession_WhenTheSessionIsUnknown_ThrowsWithoutJoiningAGroup()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.GetAsync(SessionId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new KeyNotFoundException("gone"));
        using var fixture = CreateHub(service);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeSession_WithAnEmptySessionId_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Service());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(Guid.Empty, afterSeq: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeSession_WithANegativeWatermark_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Service());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: -1)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeSession_WhenTheFeatureIsDisabled_ThrowsWithoutReachingTheService()
    {
        var service = Service();
        using var fixture = CreateHub(service, enabled: false);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Empty(service.ReceivedCalls());
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task UnsubscribeSession_LeavesTheSessionGroup()
    {
        using var fixture = CreateHub(Service());

        await fixture.Hub.UnsubscribeSession(SessionId).ConfigureAwait(false);

        await fixture.Groups.Received(1).RemoveFromGroupAsync("connection", $"work-session-{SessionId:N}", Arg.Any<CancellationToken>());
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(WorkSessionHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    private static IWorkSessionService Service(IReadOnlyList<WorkSessionEventDto>? events = null)
    {
        var service = Substitute.For<IWorkSessionService>();
        service.GetAsync(SessionId, Arg.Any<CancellationToken>())
               .Returns(new WorkSessionDetail(SessionId,
                   "title",
                   "objective",
                   AgentWorkSessionKind.Research,
                   AgentWorkSessionStatus.Running,
                   Guid.NewGuid(),
                   Guid.NewGuid(),
                   CurrentTaskId,
                   StepCount: 3,
                   MaxStepsPerRun: 25,
                   LastCheckpointId: null,
                   LastSequence: 9,
                   Version: 2,
                   CreatedUtc: 1,
                   UpdatedUtc: 2));
        service.ListEventsAsync(SessionId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(events ?? []);
        return service;
    }

    private static WorkSessionEventDto Event(long sequence) =>
        new(Guid.NewGuid(), sequence, Step: 1, "step.started", DetailJson: null, Outcome: null, OccurredUtc: 10, OperationId: null);

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IWorkSessionService service, bool enabled = true)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new WorkSessionHub(service,
            Options.Create(new WorkSessionOptions
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

    private sealed record HubFixture(WorkSessionHub Hub, IGroupManager Groups) : IDisposable
    {
        public void Dispose() =>
            Hub.Dispose();
    }
}
