namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>
///     Decides where the container bridge listens and what a container is told to call.
///     <para>
///         A container on an engine-owned bridge network cannot reach the host's loopback — that is the entire reason
///         this type exists — so the bridge binds a LAN-facing host address instead. The detection is the
///         default-route heuristic: the first interface that is up, is not loopback, is not a tunnel, has a gateway
///         and carries an IPv4 address. An explicit <see cref="ContainerBridgeOptions.BindAddress" /> short-circuits
///         all of it, mirroring how <c>ExternalApps:ContainerIdentity</c> outranks the daemon-derived identity.
///     </para>
///     <para>
///         Nothing here probes the network or opens a socket: the interface list is the one input, taken through
///         <see cref="SnapshotInterfaces" /> so every rule below can be tested against machines this one is not.
///     </para>
/// </summary>
public static class ContainerBridgeEndpointResolver
{
    /// <summary>
    ///     The name a Docker Desktop VM resolves to the host it runs on. On Windows and macOS the daemon lives in a
    ///     Linux VM, so the host's own LAN address names the host only by accident of routing, while this alias names
    ///     it by contract.
    /// </summary>
    internal const string DesktopHostAlias = "host.docker.internal";

    /// <summary>
    ///     Whether this host runs Docker Desktop, inferred from the operating system exactly as
    ///     <c>ExternalAppContainerIdentity</c> infers it — no daemon flag reports it.
    /// </summary>
    public static bool HostRunsDockerDesktop()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    ///     Resolves the bridge endpoint for this machine, or <see langword="null" /> when no interface qualifies and
    ///     the bridge therefore must not start. Never throws: a node with no usable interface still boots.
    /// </summary>
    public static ResolvedContainerBridgeEndpoint? Resolve(ContainerBridgeOptions options, bool hostRunsDockerDesktop)
    {
        return Resolve(options, hostRunsDockerDesktop, SnapshotInterfaces());
    }

    /// <summary>The resolution as a pure function of its inputs.</summary>
    internal static ResolvedContainerBridgeEndpoint? Resolve(ContainerBridgeOptions options,
        bool hostRunsDockerDesktop,
        IReadOnlyList<HostInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (SelectBindAddress(options.BindAddress, interfaces) is not { } bindAddress)
        {
            return null;
        }

        return new ResolvedContainerBridgeEndpoint(bindAddress,
            options.Port,
            BuildContainerFacingEndpoint(hostRunsDockerDesktop, bindAddress, options.Port));
    }

    /// <summary>
    ///     The address the listener binds, or <see langword="null" /> when nothing qualifies. An explicit option wins
    ///     on every host and never falls back to detection: falling back would bind an interface the operator did not
    ///     name. A wildcard is refused there for the same reason the bind guard exists — the guard is handed one
    ///     address, and a wildcard bind is not one address. IPv4 only, in both the configured and the detected path.
    ///     <para>
    ///         A configured address this host does not own resolves to nothing rather than to itself. Kestrel cannot
    ///         bind an address no interface carries, and it fails the whole host when it tries, so a typo in
    ///         <c>ContainerBridge:BindAddress</c> would otherwise cost the node its entire boot. The posture every
    ///         other path here holds is that a node with no usable bridge still boots without one.
    ///     </para>
    /// </summary>
    internal static IPAddress? SelectBindAddress(string? bindAddressOption, IReadOnlyList<HostInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        if (!string.IsNullOrWhiteSpace(bindAddressOption))
        {
            if (!IPAddress.TryParse(bindAddressOption.Trim(), out var configured)
                || configured.AddressFamily != AddressFamily.InterNetwork
                || configured.Equals(IPAddress.Any)
                || configured.Equals(IPAddress.Broadcast)
                || configured.Equals(IPAddress.None))
            {
                return null;
            }

            return CollectUnicastAddresses(interfaces).Contains(configured) ? configured : null;
        }

        // Gateway-bearing first: an interface that routes off this machine is the one the container network reaches
        // back through. Only if this host has no default route at all does a gateway-less interface (a container
        // bridge of the daemon's own, a virtual adapter) become the answer — hence two passes rather than one.
        return FirstIPv4Address(interfaces, gatewayBearingOnly: true) ?? FirstIPv4Address(interfaces, gatewayBearingOnly: false);
    }

    /// <summary>
    ///     The <c>host:port</c> an application container is given. On a Linux daemon that is the bound host address
    ///     itself; on Docker Desktop it is the alias, because the daemon is in a VM whose idea of the host's address
    ///     is not this machine's.
    ///     <para>
    ///         This is a claim about the ADDRESS a container dials, and about nothing else. Whether the connection is
    ///         then admitted depends on the source address the daemon gives it, which differs between rootless and
    ///         rootful: only rootless is validated. ADR 0011 records that, and the peer guard names it in its warning.
    ///     </para>
    /// </summary>
    internal static string BuildContainerFacingEndpoint(bool hostRunsDockerDesktop, IPAddress bindAddress, int port)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        return hostRunsDockerDesktop
            ? string.Create(CultureInfo.InvariantCulture, $"{DesktopHostAlias}:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{bindAddress}:{port}");
    }

    /// <summary>
    ///     Every unicast address this host answers on, in both families, drawn from <paramref name="interfaces" />.
    ///     The same-host peer guard compares a connection's remote address against this.
    /// </summary>
    internal static IReadOnlyList<IPAddress> CollectUnicastAddresses(IReadOnlyList<HostInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        var addresses = new List<IPAddress>();
        foreach (var candidate in interfaces)
        {
            addresses.AddRange(candidate.UnicastAddresses);
        }

        return addresses;
    }

    /// <summary>
    ///     The one thin adapter over <see cref="NetworkInterface" />. An interface whose properties the platform
    ///     refuses to report is skipped rather than failing the whole snapshot: one unreadable adapter must not cost
    ///     the node its bridge.
    /// </summary>
    internal static IReadOnlyList<HostInterfaceSnapshot> SnapshotInterfaces()
    {
        var snapshots = new List<HostInterfaceSnapshot>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                // The platform cannot describe this adapter. It contributes neither a bind candidate nor a host
                // address, and every other adapter still does.
                continue;
            }
            catch (PlatformNotSupportedException)
            {
                // Same verdict, for an adapter type this platform models but cannot query.
                continue;
            }

            var addresses = new List<IPAddress>(properties.UnicastAddresses.Count);
            foreach (var unicast in properties.UnicastAddresses)
            {
                addresses.Add(unicast.Address);
            }

            snapshots.Add(new HostInterfaceSnapshot(networkInterface.OperationalStatus == OperationalStatus.Up,
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp,
                properties.GatewayAddresses.Count > 0,
                addresses));
        }

        return snapshots;
    }

    private static IPAddress? FirstIPv4Address(IReadOnlyList<HostInterfaceSnapshot> interfaces, bool gatewayBearingOnly)
    {
        foreach (var candidate in interfaces)
        {
            if (candidate is not { IsUp: true, IsLoopback: false, IsTunnel: false }
                || (gatewayBearingOnly && !candidate.HasGateway))
            {
                continue;
            }

            foreach (var address in candidate.UnicastAddresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }
            }
        }

        return null;
    }
}
