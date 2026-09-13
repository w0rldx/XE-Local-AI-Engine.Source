namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net;
using System.Net.Sockets;

/// <summary>
///     The two questions that have to be answered before the bridge's URL is appended to the host's bind list, both
///     of which are about THIS machine at THIS moment rather than about the configuration.
///     <para>
///         They live here rather than on <c>ContainerBridgeEndpointResolver</c> on purpose: that type is a pure
///         function of an interface snapshot, which is what makes its rules testable against machines this one is
///         not. Opening a socket there would end that property. The resolver decides what the bridge WOULD be; this
///         decides whether the node can actually have it.
///     </para>
/// </summary>
internal static class ContainerBridgeListenerProbe
{
    /// <summary>
    ///     Whether the bridge's exact address and port can be bound right now.
    ///     <para>
    ///         The bridge port is a fixed default, and deliberately so — a container is handed the endpoint when it
    ///         is created and has to find the same port after the node restarts, which a randomised or persisted
    ///         port cannot promise. The cost of a fixed port is that a SECOND node on the same machine (another
    ///         checkout, or a desktop node beside a dev one) resolves the same address and port as the first. Without
    ///         this probe, Kestrel fails that bind, and because the bridge URL travels in the same bind list as the
    ///         loopback one, the whole host fails to start: a second checkout would lose its UI and API entirely.
    ///         That breaks the isolated-checkout contract, so the second node opens no bridge and boots.
    ///     </para>
    ///     <para>
    ///         No <c>SO_REUSEADDR</c>, and deliberately none: the question is whether Kestrel will succeed, and a
    ///         probe more permissive than the real bind would answer yes and still let the host die. The option is
    ///         not merely unnecessary here, it is the wrong direction — a .NET socket is created with
    ///         <c>SO_REUSEADDR</c> clear and Kestrel's listening socket takes that default too, so a probe with
    ///         these defaults answers what Kestrel answers BY CONSTRUCTION and can never be the stricter of the two.
    ///         The restart case the option is usually reached for does not arise: a port whose previous listener
    ///         closed leaves a <c>TIME_WAIT</c> entry that Linux lets the next bind have anyway, which
    ///         <c>ContainerBridgeListenerProbeTests</c> pins against a real Kestrel host rather than by assertion.
    ///     </para>
    /// </summary>
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
    ///     Whether the bridge's port is already claimed by one of the host's own bind URLs. This is the case the
    ///     socket probe CANNOT see, because at this point in startup the node has not bound its loopback listener
    ///     yet — the port is free, and would stop being free a moment later.
    ///     <para>
    ///         It matters because the branch predicate would then be ambiguous: a desktop launch given
    ///         <c>--port 18790</c> puts the loopback listener on the bridge's port, and ordinary SPA and API requests
    ///         would arrive on a port the bridge claims. Refusing to open the bridge is the answer that keeps the
    ///         node's own surface working; the bridge is the optional half.
    ///     </para>
    /// </summary>
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
