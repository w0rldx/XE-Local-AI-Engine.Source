namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The set the bridge's peer guard decides on: this computer's own addresses, kept current on a clock the test
///     owns rather than a real minute.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerBridgeAddressWatcherTests
{
    [Test]
    public void IsSameHost_AcceptsBothLoopbacksEvenWhenNoInterfaceReportsThem()
    {
        using var watcher = CreateWatcher(new ManualTimeProvider(), static () => []);

        AssertEx.True(watcher.IsSameHost(IPAddress.Loopback), "IPv4 loopback is always this computer.");
        AssertEx.True(watcher.IsSameHost(IPAddress.IPv6Loopback), "IPv6 loopback is always this computer.");
    }

    [Test]
    public void IsSameHost_AcceptsAnAddressThisHostOwnsAndRefusesEveryOther()
    {
        using var watcher = CreateWatcher(new ManualTimeProvider(), static () => [IPAddress.Parse("192.168.1.10")]);

        AssertEx.True(watcher.IsSameHost(IPAddress.Parse("192.168.1.10")), "The host's own unicast address is this computer.");
        AssertEx.False(watcher.IsSameHost(IPAddress.Parse("192.168.1.11")), "Another machine on the same subnet is not this computer.");
        AssertEx.False(watcher.IsSameHost(IPAddress.Parse("2001:db8::99")), "An unrelated IPv6 peer is not this computer.");
    }

    /// <summary>
    ///     A dual-stack listener reports an IPv4 peer as <c>::ffff:a.b.c.d</c>, which no interface enumeration ever
    ///     returns. Without the fold, the guard would refuse every container on such a listener.
    /// </summary>
    [Test]
    public void IsSameHost_FoldsAnIPv4MappedIPv6PeerBackToIPv4()
    {
        using var watcher = CreateWatcher(new ManualTimeProvider(), static () => [IPAddress.Parse("192.168.1.10")]);

        AssertEx.True(watcher.IsSameHost(IPAddress.Parse("::ffff:192.168.1.10")), "An IPv4-mapped IPv6 peer is the same peer as its IPv4 form.");
        AssertEx.True(watcher.IsSameHost(IPAddress.Parse("::ffff:127.0.0.1")), "The mapped form of loopback is still loopback.");
        AssertEx.False(watcher.IsSameHost(IPAddress.Parse("::ffff:203.0.113.7")), "Mapping does not make a foreign peer local.");
    }

    [Test]
    public void IsSameHost_RefusesAConnectionWithNoPeerAddress()
    {
        using var watcher = CreateWatcher(new ManualTimeProvider(), static () => [IPAddress.Parse("192.168.1.10")]);

        AssertEx.False(watcher.IsSameHost(remoteAddress: null), "The bridge is only ever reached over a real socket, so 'no peer' is not a peer it can vouch for.");
    }

    /// <summary>
    ///     The cadence itself, driven off a clock the test owns. It proves the hosted loop re-reads the host's
    ///     addresses — a container daemon creates and destroys bridge interfaces while the node runs, so a set frozen
    ///     at boot would refuse a container that came up on an interface created after it.
    /// </summary>
    [Test]
    public async Task Watcher_AsAHostedService_RereadsTheHostsAddressesOnceEveryInterval()
    {
        var time = new ManualTimeProvider();
        var addresses = new List<IPAddress>
        {
            IPAddress.Parse("192.168.1.10")
        };
        using var watcher = CreateWatcher(time, () => addresses.ToArray());

        await watcher.StartAsync(CancellationToken.None);
        AssertEx.True(watcher.IsSameHost(IPAddress.Parse("192.168.1.10")), "The first read happens at start, before the listener can take a connection.");

        addresses[0] = IPAddress.Parse("172.18.0.1");
        AssertEx.False(watcher.IsSameHost(IPAddress.Parse("172.18.0.1")), "Nothing may be observed before the interval elapses.");

        // Advancing before the timer is armed moves the clock past a window nothing was waiting on.
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TestBudgets.Contended, "The watcher never armed its refresh timer.");

        time.Advance(ContainerBridgeAddressWatcher.RefreshInterval);

        await AssertEx.EventuallyAsync(() => watcher.IsSameHost(IPAddress.Parse("172.18.0.1")),
                          TestBudgets.Contended,
                          "One interval elapsed and the watcher never re-read the host's addresses.");

        await watcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Registration is not the flag: the watcher is registered on every node, and an engine with the bridge off
    ///     must not arm a timer that re-reads interfaces forever for a listener that was never opened.
    /// </summary>
    [Test]
    public async Task Watcher_WhenTheBridgeIsOff_ArmsNoTimer()
    {
        var time = new ManualTimeProvider();
        using var watcher = new ContainerBridgeAddressWatcher(Options.Create(new ContainerBridgeOptions
            {
                Enabled = false
            }),
            time,
            NullLogger<ContainerBridgeAddressWatcher>.Instance,
            static () => [IPAddress.Parse("192.168.1.10")]);

        await watcher.StartAsync(CancellationToken.None);
        await AssertEx.SettleAsync();

        AssertEx.Equal(0, time.ArmedTimerCount, "A disabled bridge must not run a refresh loop.");

        await watcher.StopAsync(CancellationToken.None);
    }

    private static ContainerBridgeAddressWatcher CreateWatcher(TimeProvider timeProvider, Func<IReadOnlyList<IPAddress>> addressSource)
    {
        return new ContainerBridgeAddressWatcher(Options.Create(new ContainerBridgeOptions
            {
                Enabled = true
            }),
            timeProvider,
            NullLogger<ContainerBridgeAddressWatcher>.Instance,
            addressSource);
    }
}
