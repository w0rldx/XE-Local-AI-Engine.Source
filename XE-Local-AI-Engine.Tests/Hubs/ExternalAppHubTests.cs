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
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The subscription side of the instance hub. The store is the replay authority — the same
///     <c>ListEventsAsync</c> the events endpoint pages — so what these cases pin is the ORDER of the join and the
///     read, the watermark the replay honours, and that nothing reaches a group before the instance is known to exist.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppHubTests
{
    private const int ReplayCap = 200;

    private static readonly Guid InstanceId = ExternalAppEndpointPayloads.InstanceId;

    [Test]
    public async Task Subscribe_JoinsTheGroupBeforeReadingTheReplay()
    {
        var store = Store();
        using var fixture = CreateHub(store, Apps());

        _ = await fixture.Hub.Subscribe(InstanceId, afterSequence: 0).ConfigureAwait(false);

        // The other order leaves a window in which a change published between the read and the join reaches nobody.
        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync("connection", $"external-app-{InstanceId:N}", Arg.Any<CancellationToken>());
            store.ListEventsAsync(InstanceId, 0, ReplayCap + 1, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Subscribe_ReturnsTheInstanceStateItsWatermarkAndTheEventsAfterIt()
    {
        using var fixture = CreateHub(Store([Event(8), Event(9)]), Apps());

        var snapshot = await fixture.Hub.Subscribe(InstanceId, afterSequence: 7).ConfigureAwait(false);

        AssertEx.Equal(InstanceId, snapshot.InstanceId);
        AssertEx.Equal("Running", snapshot.Status);
        AssertEx.Equal("Running", snapshot.DesiredState);
        AssertEx.Null(snapshot.FailureCategory);
        AssertEx.Equal(expected: 42L, snapshot.LastSequence, "the watermark is the instance's own last sequence.");
        AssertEx.Equal(expected: 2, snapshot.Events.Count);
        AssertEx.Equal(expected: 8L, snapshot.Events[0].Sequence, "the replay is ascending.");
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task Subscribe_ForAFailedInstance_CarriesTheFailureCategory()
    {
        var summary = ExternalAppEndpointPayloads.Summary(ExternalAppInstanceStatus.Failed) with
        {
            FailureCategory = ExternalAppFailureCategory.ImagePullFailed
        };
        using var fixture = CreateHub(Store(), Apps(summary));

        var snapshot = await fixture.Hub.Subscribe(InstanceId, afterSequence: 0).ConfigureAwait(false);

        AssertEx.Equal("Failed", snapshot.Status);
        AssertEx.Equal("ImagePullFailed", snapshot.FailureCategory);
    }

    [Test]
    public async Task Subscribe_AtTheReplayCap_IsNotTruncated()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayCap).Select(sequence => Event(sequence))]), Apps());

        var snapshot = await fixture.Hub.Subscribe(InstanceId, afterSequence: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task Subscribe_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        using var fixture = CreateHub(Store([.. Enumerable.Range(1, ReplayCap + 1).Select(sequence => Event(sequence))]), Apps());

        var snapshot = await fixture.Hub.Subscribe(InstanceId, afterSequence: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayCap, snapshot.Events.Count);
        AssertEx.True(snapshot.ReplayTruncated, "the cap is observed one row over it, never inferred from a full page.");
    }

    [Test]
    public async Task Subscribe_WhenTheInstanceIsUnknown_ThrowsWithoutJoiningAGroup()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.GetAsync(InstanceId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new ExternalAppNotFoundException("gone"));
        using var fixture = CreateHub(Store(), apps);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.Subscribe(InstanceId, afterSequence: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task Subscribe_WithAnEmptyInstanceId_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store(), Apps());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.Subscribe(Guid.Empty, afterSequence: 0)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task Subscribe_WithANegativeWatermark_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Store(), Apps());

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.Subscribe(InstanceId, afterSequence: -1)).ConfigureAwait(false);

        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    /// <summary>The flag is checked before the service is touched, so a disabled node cannot be probed through the hub.</summary>
    [Test]
    public async Task Subscribe_WhenTheFeatureIsDisabled_ThrowsWithoutReachingTheService()
    {
        var apps = Apps();
        using var fixture = CreateHub(Store(), apps, enabled: false);

        _ = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.Subscribe(InstanceId, afterSequence: 0)).ConfigureAwait(false);

        AssertEx.Empty(apps.ReceivedCalls());
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    /// <summary>
    ///     The join happens before the replay read, so a failing read must UNDO it. A membership left behind on a
    ///     subscribe that threw keeps pushing this instance's changes at a connection that got no watermark and holds
    ///     no handle to unsubscribe with.
    /// </summary>
    [Test]
    public async Task Subscribe_WhenTheReplayReadFails_LeavesTheGroupAndRethrows()
    {
        var store = Substitute.For<IExternalAppInstanceStore>();
        store.ListEventsAsync(InstanceId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new InvalidOperationException("the replay read failed."));
        using var fixture = CreateHub(store, Apps());

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => fixture.Hub.Subscribe(InstanceId, afterSequence: 0))
                          .ConfigureAwait(false);

        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync("connection", $"external-app-{InstanceId:N}", Arg.Any<CancellationToken>());
            fixture.Groups.RemoveFromGroupAsync("connection", $"external-app-{InstanceId:N}", Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Unsubscribe_LeavesTheInstanceGroup()
    {
        using var fixture = CreateHub(Store(), Apps());

        await fixture.Hub.Unsubscribe(InstanceId).ConfigureAwait(false);

        await fixture.Groups.Received(1).RemoveFromGroupAsync("connection", $"external-app-{InstanceId:N}", Arg.Any<CancellationToken>());
    }

    /// <summary>Subscribe and Unsubscribe are the whole server-to-client surface: nothing else is callable.</summary>
    [Test]
    public void Hub_DeclaresOnlyTheSubscriptionMethods()
    {
        var methods = typeof(ExternalAppHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                            .Select(static method => method.Name)
                                            .ToArray();

        AssertEx.Equal(expected: 2, methods.Length, $"the hub declares {string.Join(", ", methods)}.");
        AssertEx.Contains(methods, nameof(ExternalAppHub.Subscribe));
        AssertEx.Contains(methods, nameof(ExternalAppHub.Unsubscribe));
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(ExternalAppHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    private static IExternalAppInstanceStore Store(IReadOnlyList<ExternalAppInstanceEventSnapshot>? events = null)
    {
        var store = Substitute.For<IExternalAppInstanceStore>();
        store.ListEventsAsync(InstanceId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(events ?? []);
        return store;
    }

    private static IExternalAppService Apps(ExternalAppInstanceSummary? summary = null)
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.GetAsync(InstanceId, Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.Detail(summary));
        return apps;
    }

    private static ExternalAppInstanceEventSnapshot Event(long sequence) =>
        new(Guid.NewGuid(), InstanceId, sequence, ExternalAppInstanceEventKind.Started, DetailJson: null, OccurredAtUtc: 100);

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IExternalAppInstanceStore store, IExternalAppService apps, bool enabled = true)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new ExternalAppHub(apps,
            store,
            Options.Create(new ExternalAppsOptions
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

    private sealed record HubFixture(ExternalAppHub Hub, IGroupManager Groups) : IDisposable
    {
        public void Dispose() =>
            Hub.Dispose();
    }
}
