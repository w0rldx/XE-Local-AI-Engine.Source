namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Globalization;
using System.Net;

/// <summary>One host network interface, reduced to the four facts the bridge decides on.</summary>
/// <remarks>
///     It exists so the decision is a pure function of its inputs: <c>System.Net.NetworkInformation</c> describes
///     only the interfaces THIS machine has, and the rules for the machines it is not have to be testable rather
///     than reasoned about.
/// </remarks>
public sealed class HostInterfaceSnapshot
{
    /// <summary>Whether the interface is operationally up.</summary>
    public required bool IsUp { get; init; }

    /// <summary>Whether the interface is the loopback interface.</summary>
    public required bool IsLoopback { get; init; }

    /// <summary>Whether the interface is a tunnel or dial-up link, which no container network reaches back through.</summary>
    public required bool IsTunnel { get; init; }

    /// <summary>Whether the interface has at least one gateway address, i.e. whether it routes off this machine.</summary>
    public required bool HasGateway { get; init; }

    /// <summary>Every unicast address assigned to the interface, in both families.</summary>
    public required IReadOnlyList<IPAddress> UnicastAddresses { get; init; }
}

/// <summary>
///     Where the bridge listens, and the string an application container is given to reach it. The two are NOT the
///     same value on every platform, which is the whole reason this is a record rather than an address and a port.
/// </summary>
/// <param name="BindAddress">The host IPv4 address Kestrel binds the bridge listener to.</param>
/// <param name="Port">The port the bridge listener binds.</param>
/// <param name="ContainerFacingEndpoint">The <c>host:port</c> a container uses, which on Docker Desktop names the host by alias rather than by address.</param>
public sealed record ResolvedContainerBridgeEndpoint(IPAddress BindAddress, int Port, string ContainerFacingEndpoint)
{
    /// <summary>
    ///     The listener URL, in the exact shape <c>IServerAddressesFeature</c> reports it back — so the startup bind
    ///     guard can be handed this one string as the single non-loopback bind it is allowed to see.
    /// </summary>
#pragma warning disable S5332 // The bridge is a same-machine listener whose peer guard refuses any peer but this computer; TLS on a loopback-equivalent hop would buy a certificate problem, not a secret.
    public string ListenerUrl { get; } = string.Create(CultureInfo.InvariantCulture, $"http://{BindAddress}:{Port}");
#pragma warning restore S5332

    /// <summary>Whether a connection arrived on THIS listener, judged by the local end of its socket — the one fact a caller cannot forge.</summary>
    /// <remarks>
    ///     The address half is load-bearing, not belt-and-braces: a port alone is ambiguous on a node whose loopback
    ///     listener was given the bridge's port (a desktop launch with <c>--port 18790</c>), and every ordinary SPA
    ///     and API request would then route into the bridge branch. An IPv4-mapped IPv6 local address is folded back
    ///     to IPv4 first, because a dual-stack socket reports the bound IPv4 address in its mapped form and
    ///     <see cref="IPAddress.Equals(IPAddress)" /> does not treat the two spellings as equal.
    /// </remarks>
    public bool Matches(IPAddress? localAddress, int localPort)
    {
        if (localPort != Port || localAddress is null)
        {
            return false;
        }

        var normalized = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
        return normalized.Equals(BindAddress);
    }
}

/// <summary>Where the bridge listener actually ended up, published from the composition root into the services that must tell a container about it.</summary>
/// <remarks>
///     The bind address is resolved during host construction, before any service is built, and is
///     <see langword="null" /> whenever the bridge did not open. Reading it through one object rather than
///     re-resolving it per caller is what keeps every container on a node pointed at the same address.
/// </remarks>
public sealed class ContainerBridgeEndpointSource
{
    public ContainerBridgeEndpointSource(ResolvedContainerBridgeEndpoint? endpoint)
    {
        Current = endpoint;
    }

    /// <summary>The open bridge listener, or <see langword="null" /> when this node has none.</summary>
    public ResolvedContainerBridgeEndpoint? Current { get; }
}
