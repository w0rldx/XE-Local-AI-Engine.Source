namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The rules that decide where the container bridge listens and what a container is told to call. Every case is
///     driven off a fabricated interface list: <c>System.Net.NetworkInformation</c> describes only the machine the
///     test happens to run on, and the rules that matter are the ones for the machines it is not.
/// </summary>
public sealed class ContainerBridgeEndpointResolverTests
{
    [Test]
    public void BindAddress_WhenConfigured_WinsOverEveryDetectedInterface()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress("10.9.9.9",
        [
            Interface("192.168.1.10", hasGateway: true),
            Interface("10.9.9.9")
        ]);

        AssertEx.Equal(IPAddress.Parse("10.9.9.9"), selected,
            "An explicitly configured bind address the host owns must outrank detection, gateway or no gateway.");
    }

    /// <summary>
    ///     A typo in <c>ContainerBridge:BindAddress</c> must cost the node its bridge, not its boot: Kestrel cannot
    ///     bind an address no interface carries and fails the whole host when it tries. Every other path in this
    ///     feature holds the same posture — a node with no usable bridge still boots without one.
    /// </summary>
    [Test]
    public void BindAddress_WhenConfiguredAddressIsNotOnThisHost_ResolvesToNothing()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress("10.9.9.9",
        [
            Interface("192.168.1.10", hasGateway: true)
        ]);

        AssertEx.Null(selected,
            "A bind address this host does not own would fail Kestrel's bind and take the node down with it, so the bridge must decline to open.");
    }

    /// <summary>
    ///     Ownership is the test, not routability: a down interface's address is still an address this host answers
    ///     on, and an operator naming it gets the bridge they asked for rather than a silent detection fallback.
    /// </summary>
    [Test]
    public void BindAddress_WhenConfiguredAddressIsOwnedByADownInterface_IsStillAccepted()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress("10.9.9.9",
        [
            Interface("192.168.1.10", hasGateway: true),
            Interface("10.9.9.9", isUp: false)
        ]);

        AssertEx.Equal(IPAddress.Parse("10.9.9.9"), selected, "An explicit bind address is the operator's decision; ownership is all this check adds.");
    }

    [Test]
    public void Resolve_WhenTheConfiguredBindAddressIsNotOnThisHost_ReturnsNothingRatherThanThrowing()
    {
        var resolved = ContainerBridgeEndpointResolver.Resolve(new ContainerBridgeOptions { Enabled = true, BindAddress = "10.9.9.9" },
            hostRunsDockerDesktop: false,
            [
                Interface("192.168.1.10", hasGateway: true)
            ]);

        AssertEx.Null(resolved, "A mistyped bind address must leave the node booting without a bridge, never dead at startup.");
    }

    /// <summary>
    ///     A configured value that is not one usable IPv4 address resolves to nothing rather than to a detected
    ///     interface: falling back would bind an interface the operator did not name, and a wildcard is not one
    ///     address the startup bind guard could be handed.
    /// </summary>
    [Test]
    [Arguments("0.0.0.0")]
    [Arguments("255.255.255.255")]
    [Arguments("::1")]
    [Arguments("fe80::1")]
    [Arguments("not-an-address")]
    public void BindAddress_WhenConfiguredValueIsUnusable_ResolvesToNothingAndNeverFallsBack(string configured)
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress(configured,
        [
            Interface("192.168.1.10", hasGateway: true)
        ]);

        AssertEx.Null(selected, $"'{configured}' is not a single routable IPv4 bind, so the bridge must not start on a detected interface instead.");
    }

    [Test]
    public void BindAddress_PrefersTheInterfaceThatRoutesOffThisMachine()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress(bindAddressOption: null,
        [
            Interface("172.17.0.1", hasGateway: false),
            Interface("192.168.1.10", hasGateway: true)
        ]);

        AssertEx.Equal(IPAddress.Parse("192.168.1.10"), selected, "The gateway-bearing interface is the one a container network reaches back through.");
    }

    [Test]
    public void BindAddress_WhenNoInterfaceHasAGateway_FallsBackToTheFirstUsableOne()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress(bindAddressOption: null,
        [
            Interface("172.17.0.1", hasGateway: false),
            Interface("172.18.0.1", hasGateway: false)
        ]);

        AssertEx.Equal(IPAddress.Parse("172.17.0.1"), selected, "A host with no default route still gets a bridge, on its first usable interface.");
    }

    [Test]
    public void BindAddress_SkipsInterfacesThatAreDownLoopbackTunnelOrIPv6Only()
    {
        var selected = ContainerBridgeEndpointResolver.SelectBindAddress(bindAddressOption: null,
        [
            Interface("10.0.0.1", isUp: false),
            Interface("127.0.0.1", isLoopback: true),
            Interface("10.8.0.1", isTunnel: true),
            Interface(ipv4: null, ipv6: "2001:db8::1"),
            Interface("192.168.1.10")
        ]);

        AssertEx.Equal(IPAddress.Parse("192.168.1.10"), selected, "Only an up, non-loopback, non-tunnel interface carrying IPv4 may host the bridge.");
    }

    [Test]
    public void Resolve_WhenNoInterfaceQualifies_ReturnsNothingRatherThanThrowing()
    {
        var resolved = ContainerBridgeEndpointResolver.Resolve(new ContainerBridgeOptions { Enabled = true },
            hostRunsDockerDesktop: false,
            [
                Interface("127.0.0.1", isLoopback: true)
            ]);

        AssertEx.Null(resolved, "A node with no usable interface must still boot; it simply gets no bridge.");
    }

    [Test]
    public void Resolve_OnALinuxDaemon_HandsContainersTheBoundAddressItself()
    {
        var resolved = AssertEx.NotNull(ContainerBridgeEndpointResolver.Resolve(new ContainerBridgeOptions { Enabled = true, Port = 18790 },
            hostRunsDockerDesktop: false,
            [
                Interface("192.168.1.10")
            ]));

        AssertEx.Equal("192.168.1.10:18790", resolved.ContainerFacingEndpoint);
        AssertEx.Equal("http://192.168.1.10:18790", resolved.ListenerUrl);
        AssertEx.Equal(18790, resolved.Port);
    }

    /// <summary>
    ///     On Windows and macOS the daemon runs inside a Linux VM, so the host's own LAN address names the host only
    ///     by accident of routing while the alias names it by contract. The listener still binds the real address.
    /// </summary>
    [Test]
    public void Resolve_OnDockerDesktop_HandsContainersTheHostAliasWhileStillBindingTheRealAddress()
    {
        var resolved = AssertEx.NotNull(ContainerBridgeEndpointResolver.Resolve(new ContainerBridgeOptions { Enabled = true, Port = 18790 },
            hostRunsDockerDesktop: true,
            [
                Interface("192.168.1.10")
            ]));

        AssertEx.Equal("host.docker.internal:18790", resolved.ContainerFacingEndpoint);
        AssertEx.Equal(IPAddress.Parse("192.168.1.10"), resolved.BindAddress);
        AssertEx.Equal("http://192.168.1.10:18790", resolved.ListenerUrl);
    }

    [Test]
    public void CollectUnicastAddresses_ReturnsEveryAddressOfEveryInterfaceInBothFamilies()
    {
        var addresses = ContainerBridgeEndpointResolver.CollectUnicastAddresses(
        [
            Interface("127.0.0.1", isLoopback: true),
            Interface("192.168.1.10", ipv6: "2001:db8::1"),
            Interface("10.0.0.5", isUp: false)
        ]);

        // Down and loopback interfaces are excluded from BINDING, never from "is this peer this computer" — a peer
        // arriving on an address of an interface this host owns is this host whatever that interface is doing.
        AssertEx.Contains(addresses, IPAddress.Parse("127.0.0.1"));
        AssertEx.Contains(addresses, IPAddress.Parse("192.168.1.10"));
        AssertEx.Contains(addresses, IPAddress.Parse("2001:db8::1"));
        AssertEx.Contains(addresses, IPAddress.Parse("10.0.0.5"));
    }

    /// <summary>
    ///     The one test that touches the real adapter: it asserts the adapter produces a snapshot at all, which is
    ///     the only thing about this machine's interfaces that is true on every machine.
    /// </summary>
    [Test]
    public void SnapshotInterfaces_DescribesThisMachineWithoutThrowing()
    {
        var interfaces = ContainerBridgeEndpointResolver.SnapshotInterfaces();

        AssertEx.NotEmpty(interfaces, "Every host has at least a loopback interface.");
    }

    private static HostInterfaceSnapshot Interface(string? ipv4,
        bool isUp = true,
        bool hasGateway = false,
        bool isLoopback = false,
        bool isTunnel = false,
        string? ipv6 = null)
    {
        var addresses = new List<IPAddress>();
        if (ipv4 is not null)
        {
            addresses.Add(IPAddress.Parse(ipv4));
        }

        if (ipv6 is not null)
        {
            addresses.Add(IPAddress.Parse(ipv6));
        }

        return new HostInterfaceSnapshot(isUp, isLoopback, isTunnel, hasGateway, addresses);
    }
}
