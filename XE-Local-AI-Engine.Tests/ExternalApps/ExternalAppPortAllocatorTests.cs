namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Real loopback sockets, because the thing under test is whether the port is actually held. A fake socket would
///     assert that the code calls something, which is the half that was never in doubt.
/// </summary>
public sealed class ExternalAppPortAllocatorTests
{
    [Test]
    public void Hold_WithAFreePreferredPort_TakesIt()
    {
        var preferred = FindFreePort();
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(8080, preferred)])]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);

        AssertEx.Equal(expected: 1, hold.Ports.Count);
        AssertEx.Equal(preferred, hold.Ports[0].HostPort);
        AssertEx.Equal(expected: 8080, hold.Ports[0].ContainerPort);
        AssertEx.Equal("app", hold.Ports[0].Service);
    }

    [Test]
    public void Hold_WhenThePreferredPortIsTaken_FallsThroughToAnEphemeralOne()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        occupied.Listen(backlog: 1);
        var taken = ((IPEndPoint)occupied.LocalEndPoint!).Port;

        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(8080, taken)])]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);

        AssertEx.NotEqual(taken, hold.Ports[0].HostPort);
        AssertEx.True(hold.Ports[0].HostPort > 0, "An ephemeral port must have been assigned.");
    }

    /// <summary>
    ///     Two services asking for one port is the collision the held listener rules out for free: the first one is
    ///     still bound when the second probes, so the second falls through rather than being handed the same port.
    /// </summary>
    [Test]
    public void Hold_WhenTwoServicesPreferTheSamePort_NeverGivesThemTheSameOne()
    {
        var preferred = FindFreePort();
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("first", ports: [ExternalAppTestManifests.UiPort(80, preferred)]),
            ExternalAppTestManifests.Service("second", ports: [ExternalAppTestManifests.UiPort(80, preferred)], image: ExternalAppTestManifests.SecondImage)
        ]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);

        AssertEx.Equal(expected: 2, hold.Ports.Count);
        AssertEx.NotEqual(hold.Ports[0].HostPort, hold.Ports[1].HostPort);
    }

    /// <summary>
    ///     The difference that decides the feature. Probe-then-close leaves a window per port that widens with every
    ///     image pulled between the probe and the create, and the window is where another process takes the port.
    /// </summary>
    [Test]
    public void Hold_KeepsThePortBoundUntilItsOwnServiceIsReleased()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("first", ports: [ExternalAppTestManifests.UiPort(80)]),
            ExternalAppTestManifests.Service("second", ports: [ExternalAppTestManifests.UiPort(80)], image: ExternalAppTestManifests.SecondImage)
        ]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);
        var first = hold.Ports[0].HostPort;
        var second = hold.Ports[1].HostPort;

        AssertEx.False(ExternalAppPortAllocator.IsBindable(first), "The held port must not be bindable by anyone else.");
        AssertEx.False(ExternalAppPortAllocator.IsBindable(second), "Every port of the attempt is held, not just the first.");

        hold.Release("first");

        AssertEx.True(ExternalAppPortAllocator.IsBindable(first), "Releasing a service must free its port for the create that follows.");
        AssertEx.False(ExternalAppPortAllocator.IsBindable(second), "Releasing one service must not release another's port.");
    }

    [Test]
    public void Release_IsIdempotentAndIgnoresAServiceThatPublishesNothing()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(80)])]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);
        hold.Release("app");
        hold.Release("app");
        hold.Release("never-declared");

        AssertEx.True(ExternalAppPortAllocator.IsBindable(hold.Ports[0].HostPort), "The port must stay released.");
    }

    [Test]
    public void Dispose_ReleasesEveryPortStillHeld()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(80)])]);

        int port;
        using (var hold = ExternalAppPortAllocator.Hold(manifest))
        {
            port = hold.Ports[0].HostPort;
            AssertEx.False(ExternalAppPortAllocator.IsBindable(port), "Held while the attempt runs.");
        }

        AssertEx.True(ExternalAppPortAllocator.IsBindable(port), "A failed attempt must not leak its reservations.");
    }

    /// <summary>
    ///     <c>XE_UI_HOST_PORT_&lt;service&gt;</c> is one value per service, so the map carries each service's first
    ///     published port. A service that publishes nothing is absent rather than zero.
    /// </summary>
    [Test]
    public void ByService_CarriesTheFirstPublishedPortOfEachService()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(80), ExternalAppTestManifests.UiPort(443)]),
            ExternalAppTestManifests.Service("quiet", image: ExternalAppTestManifests.SecondImage)
        ]);

        using var hold = ExternalAppPortAllocator.Hold(manifest);

        AssertEx.Equal(expected: 2, hold.Ports.Count);
        AssertEx.Equal(hold.Ports[0].HostPort, hold.ByService["app"]);
        AssertEx.False(hold.ByService.ContainsKey("quiet"), "A service that publishes nothing has no host port.");
    }

    [Test]
    public void IsBindable_IsTrueForAPortNobodyHolds()
    {
        AssertEx.True(ExternalAppPortAllocator.IsBindable(FindFreePort()), "A port just released must be bindable.");
    }

    private static int FindFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
