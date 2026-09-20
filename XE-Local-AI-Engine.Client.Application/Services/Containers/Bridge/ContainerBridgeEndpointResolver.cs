namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>Decides where the container bridge listens and what a container is told to call.</summary>
/// <remarks>
///     A container on an engine-owned bridge network cannot reach the host's loopback — the entire reason this type
///     exists — so the bridge binds a LAN-facing host address instead. The detection is the default-route heuristic:
///     the first interface that is up, is not loopback, is not a tunnel, has a gateway and carries an IPv4 address.
///     An explicit <see cref="ContainerBridgeOptions.BindAddress" /> short-circuits all of it. Nothing here probes
///     the network: the interface list from <see cref="SnapshotInterfaces" /> is the one input.
/// </remarks>
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

    /// <summary>The address the listener binds, or <see langword="null" /> when nothing qualifies; IPv4 only on both the configured and the detected path.</summary>
    /// <remarks>
    ///     An explicit option wins on every host and never falls back to detection, which would bind an interface
    ///     the operator did not name, and a wildcard is refused there because the bind guard is handed ONE address.
    ///     A configured address this host does not own resolves to nothing rather than to itself: Kestrel fails the
    ///     whole host when it cannot bind, so a typo in <c>ContainerBridge:BindAddress</c> would otherwise cost the
    ///     node its boot, against the posture that a node with no usable bridge still boots without one.
    /// </remarks>
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
        // back through. A gateway-less one answers only when this host has no default route at all — hence two passes.
        return FirstIPv4Address(interfaces, gatewayBearingOnly: true) ?? FirstIPv4Address(interfaces, gatewayBearingOnly: false);
    }

    /// <summary>The <c>host:port</c> an application container is given: the bound host address on a Linux daemon, the alias on Docker Desktop.</summary>
    /// <remarks>
    ///     Docker Desktop runs the daemon in a VM whose idea of the host's address is not this machine's. This is a
    ///     claim about the ADDRESS a container dials and nothing else: whether the connection is then admitted
    ///     depends on the source address the daemon gives it, which differs between rootless and rootful. Only
    ///     rootless is validated — ADR 0011 records that, and the peer guard names it in its warning.
    /// </remarks>
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

            snapshots.Add(new HostInterfaceSnapshot
            {
                IsUp = networkInterface.OperationalStatus == OperationalStatus.Up,
                IsLoopback = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                IsTunnel = networkInterface.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp,
                HasGateway = properties.GatewayAddresses.Count > 0,
                UnicastAddresses = addresses
            });
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
