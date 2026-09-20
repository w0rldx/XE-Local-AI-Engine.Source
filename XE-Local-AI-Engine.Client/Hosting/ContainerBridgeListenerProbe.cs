namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net;
using System.Net.Sockets;

/// <summary>
///     The two questions that have to be answered before the bridge's URL is appended to the host's bind list, both
///     of which are about THIS machine at THIS moment rather than about the configuration.
/// </summary>
/// <remarks>
///     They live here rather than on <c>ContainerBridgeEndpointResolver</c> on purpose: that type is a pure function
///     of an interface snapshot, which is what makes its rules testable against machines this one is not, and opening
///     a socket there would end that property. The resolver decides what the bridge WOULD be; this decides whether the
///     node can actually have it. See docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
/// </remarks>
internal static class ContainerBridgeListenerProbe
{
    /// <summary>
    ///     Whether the bridge's exact address and port can be bound right now.
    /// </summary>
    /// <remarks>
    ///     No <c>SO_REUSEADDR</c>, and deliberately none: a probe more permissive than the real bind would answer yes
    ///     and still let the host die. A .NET socket is created with <c>SO_REUSEADDR</c> clear and Kestrel's listening
    ///     socket takes that default too, so these defaults answer what Kestrel answers BY CONSTRUCTION.
    ///     <c>ContainerBridgeListenerProbeTests</c> pins that against a real Kestrel host. Why a fixed port needs this
    ///     probe: docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
    /// </remarks>
    internal static bool IsPortAvailable(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            // Address in use, or an address this host will not let us have. Either way the bridge cannot open.
            return false;
        }
    }

    /// <summary>
    ///     Whether the bridge's port is already claimed by one of the host's own bind URLs.
    /// </summary>
    /// <remarks>
    ///     This is the case the socket probe CANNOT see: at this point in startup the node has not bound its loopback
    ///     listener yet, so the port is free and would stop being free a moment later. A desktop launch given
    ///     <c>--port 18790</c> would then make the branch predicate ambiguous — see
    ///     docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
    /// </remarks>
    internal static bool CollidesWithHostingUrls(IReadOnlyList<string> hostingUrls, int port)
    {
        ArgumentNullException.ThrowIfNull(hostingUrls);

        foreach (var url in hostingUrls)
        {
            if (BindingAddress.Parse(url).Port == port)
            {
                return true;
            }
        }

        return false;
    }
}
