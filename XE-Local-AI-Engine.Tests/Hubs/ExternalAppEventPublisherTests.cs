namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The hub-backed publisher. What it pins is the wire vocabulary — every event kind crossing as its own
///     lowerCamelCase literal — the group a message is addressed to, and that neither payload carries anything but
///     identifiers and counters.
/// </summary>
public sealed class ExternalAppEventPublisherTests
{
    /// <summary>
    ///     Asserted against the literals, not against <c>kind.ToString()</c>: the client switches on these strings, so
    ///     a capitalised name would match no arm and silently stop updating the view, and nothing else would catch it.
    /// </summary>
    [Test]
    [Arguments(ExternalAppInstanceEventKind.Installed, "installed")]
    [Arguments(ExternalAppInstanceEventKind.StartRequested, "startRequested")]
    [Arguments(ExternalAppInstanceEventKind.Started, "started")]
    [Arguments(ExternalAppInstanceEventKind.StopRequested, "stopRequested")]
    [Arguments(ExternalAppInstanceEventKind.Stopped, "stopped")]
    [Arguments(ExternalAppInstanceEventKind.Restarted, "restarted")]
    [Arguments(ExternalAppInstanceEventKind.UpdateRequested, "updateRequested")]
    [Arguments(ExternalAppInstanceEventKind.Updated, "updated")]
    [Arguments(ExternalAppInstanceEventKind.ResetRequested, "resetRequested")]
    [Arguments(ExternalAppInstanceEventKind.Reset, "reset")]
    [Arguments(ExternalAppInstanceEventKind.UninstallRequested, "uninstallRequested")]
    [Arguments(ExternalAppInstanceEventKind.Uninstalled, "uninstalled")]
    [Arguments(ExternalAppInstanceEventKind.Failed, "failed")]
    [Arguments(ExternalAppInstanceEventKind.RestoredOnBoot, "restoredOnBoot")]
    [Arguments(ExternalAppInstanceEventKind.StoppedUnexpectedly, "stoppedUnexpectedly")]
    [Arguments(ExternalAppInstanceEventKind.PermissionAccepted, "permissionAccepted")]
    public async Task PublishAsync_SendsEachEventKindAsItsOwnLowerCamelCaseLiteral(ExternalAppInstanceEventKind kind, string expected)
    {
        var instanceId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy);
        var hubContext = Substitute.For<IHubContext<ExternalAppHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new ExternalAppEventPublisher(hubContext);

        await publisher.PublishAsync(instanceId, sequence: 7, kind, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        await proxy.Received(1)
                   .SendCoreAsync(ExternalAppHubEvents.Changed,
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is ExternalAppChanged
                                                      && ((ExternalAppChanged)arguments[0]!).Kind == expected),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The vocabulary above is complete. A member added to the enum fails here rather than shipping unmapped, which
    ///     is a 500 at publish time on whichever operation first mints it.
    /// </summary>
    [Test]
    public void EveryEventKind_HasAWireLiteral() =>
        AssertEx.Equal(expected: 16,
            Enum.GetValues<ExternalAppInstanceEventKind>().Length,
            "add the new kind to ExternalAppEventPublisher.ToWireKind and to the arguments above.");

    [Test]
    public async Task PublishAsync_SendsTheSequenceAndTheStatusToTheInstanceGroup()
    {
        var instanceId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group($"external-app-{instanceId:N}").Returns(proxy);
        var hubContext = Substitute.For<IHubContext<ExternalAppHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new ExternalAppEventPublisher(hubContext);

        await publisher.PublishAsync(instanceId, sequence: 42, ExternalAppInstanceEventKind.StartRequested, ExternalAppInstanceStatus.Starting)
                       .ConfigureAwait(false);

        await proxy.Received(1)
                   .SendCoreAsync("externalAppChanged",
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is ExternalAppChanged
                                                      && ((ExternalAppChanged)arguments[0]!).InstanceId == instanceId
                                                      && ((ExternalAppChanged)arguments[0]!).Sequence == 42
                                                      && ((ExternalAppChanged)arguments[0]!).Kind == "startRequested"
                                                      && ((ExternalAppChanged)arguments[0]!).Status == "Starting"),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishAsync_NeverSendsToAnotherInstancesGroup()
    {
        var instanceId = Guid.NewGuid();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hubContext = Substitute.For<IHubContext<ExternalAppHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new ExternalAppEventPublisher(hubContext);

        await publisher.PublishAsync(instanceId, sequence: 1, ExternalAppInstanceEventKind.Started, ExternalAppInstanceStatus.Running)
                       .ConfigureAwait(false);

        _ = clients.Received(1).Group($"external-app-{instanceId:N}");
        AssertEx.Equal(expected: 1, clients.ReceivedCalls().Count());
    }

    /// <summary>Renaming or adding an enum member must not be able to change the wire contract silently.</summary>
    [Test]
    public async Task PublishAsync_WithAKindTheWireDoesNotKnow_Throws()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hubContext = Substitute.For<IHubContext<ExternalAppHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new ExternalAppEventPublisher(hubContext);

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => publisher.PublishAsync(Guid.NewGuid(),
                              sequence: 1,
                              (ExternalAppInstanceEventKind)99,
                              ExternalAppInstanceStatus.Running))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     The registration order that makes any of this reach a browser: the application module registers the no-op
    ///     with <c>TryAddSingleton</c>, and the host's composition root registers this one after it.
    /// </summary>
    [Test]
    public async Task Publisher_SupersedesTheNoOpInTheHost()
    {
        await using var factory = ExternalAppEndpointPayloads.EnabledFactory();

        var publisher = factory.Services.GetRequiredService<IExternalAppEventPublisher>();

        AssertEx.True(publisher is ExternalAppEventPublisher, $"the host resolved {publisher.GetType().Name}.");
    }

    /// <summary>Pull progress is its own message name and its own payload: counters and bytes, never a layer's content.</summary>
    [Test]
    public async Task PublishPullProgressAsync_SendsTheProgressPayloadUnderItsOwnName()
    {
        var instanceId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group($"external-app-{instanceId:N}").Returns(proxy);
        var hubContext = Substitute.For<IHubContext<ExternalAppHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new ExternalAppEventPublisher(hubContext);

        await publisher.PublishPullProgressAsync(instanceId, "web", layerCount: 9, completedLayers: 3, bytes: 4_096L).ConfigureAwait(false);

        await proxy.Received(1)
                   .SendCoreAsync("externalAppPullProgress",
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is ExternalAppPullProgress
                                                      && ((ExternalAppPullProgress)arguments[0]!).InstanceId == instanceId
                                                      && ((ExternalAppPullProgress)arguments[0]!).Service == "web"
                                                      && ((ExternalAppPullProgress)arguments[0]!).LayerCount == 9
                                                      && ((ExternalAppPullProgress)arguments[0]!).CompletedLayers == 3
                                                      && ((ExternalAppPullProgress)arguments[0]!).Bytes == 4_096L),
                       Arg.Any<CancellationToken>());
    }
}
