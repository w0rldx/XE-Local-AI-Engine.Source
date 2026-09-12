namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two refusals that keep a second node on this machine booting.
///     <para>
///         The bridge URL is APPENDED to the host's bind list, so Kestrel treats a bridge it cannot bind as a failed
///         host: a second checkout would lose its loopback UI and API entirely, not merely its bridge. The port is a
///         fixed default on purpose — a container receives the endpoint when it is created and has to find the same
///         port after the node restarts — so two nodes resolving the same address and port is the expected case
///         rather than a misconfiguration, and it has to end in a warning instead of a dead node.
///     </para>
///     <para>
///         These are unit tests on the probe rather than a host-level test, and deliberately.
///         <c>Program.ResolveContainerBridgeEndpoint</c> is private, runs during host construction and needs bind
///         URLs the in-memory TestServer does not have, so a host-level test would have to reconstruct the
///         composition root rather than exercise it. What is actually worth pinning is the decision, and the
///         decision is these two functions.
///     </para>
/// </summary>
public sealed class ContainerBridgeListenerProbeTests
{
    [Test]
    public void IsPortAvailable_WhenNothingHoldsThePort_IsTrue()
    {
        var port = FreeLoopbackPort();

        AssertEx.True(ContainerBridgeListenerProbe.IsPortAvailable(IPAddress.Loopback, port),
            "A port nothing holds must be reported bindable, or a node that could have a bridge would be refused one.");
    }

    /// <summary>
    ///     The second-node case, with a real socket standing in for the first node's bridge listener. The probe must
    ///     answer no, which is what turns "this host fails to start" into "this node has no bridge".
    /// </summary>
    [Test]
    public void IsPortAvailable_WhenAnotherSocketAlreadyHoldsTheAddressAndPort_IsFalse()
    {
        var port = FreeLoopbackPort();
        using var firstNode = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        firstNode.Bind(new IPEndPoint(IPAddress.Loopback, port));
        firstNode.Listen(backlog: 1);

        AssertEx.False(ContainerBridgeListenerProbe.IsPortAvailable(IPAddress.Loopback, port),
            "A port a first node already holds must be refused; appending an unbindable URL fails the whole host, not just the bridge.");
    }

    /// <summary>
    ///     The probe must not be more permissive than the bind it predicts. `SO_REUSEADDR` would make it answer yes
    ///     where Kestrel then fails, which is the one way this guard could be worse than useless.
    /// </summary>
    [Test]
    public void IsPortAvailable_DoesNotReportABoundPortAsFreeThroughAddressReuse()
    {
        var port = FreeLoopbackPort();
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        holder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, port));
        holder.Listen(backlog: 1);

        AssertEx.False(ContainerBridgeListenerProbe.IsPortAvailable(IPAddress.Loopback, port),
            "Even against a holder that set SO_REUSEADDR, the probe must answer what Kestrel would answer.");
    }

    /// <summary>
    ///     The node-restart case, end to end and against the real server rather than against a bare socket: a bridge
    ///     that served a container leaves the port in <c>TIME_WAIT</c> when the host stops, and the next node has to
    ///     get it back. If it did not, the bridge would be dark for the whole lifetime of every process after the
    ///     first — the port is a fixed default, so there is no second port to fall back to.
    ///     <para>
    ///         Two claims, and both are needed. Kestrel must be able to rebind, which is the product requirement;
    ///         and the probe must answer exactly what Kestrel answered, which is the probe's only job. The pair is
    ///         also what makes <c>SO_REUSEADDR</c> on the probe wrong rather than merely unnecessary: the option
    ///         would make the probe the more permissive of the two, and only the more permissive one can lie.
    ///     </para>
    /// </summary>
    [Test]
    public async Task IsPortAvailable_AfterAListenerThatServedAConnectionClosed_AgreesWithWhatKestrelThenDoes()
    {
        var port = FreeLoopbackPort();

        // A served connection, so the closing side leaves a real TIME_WAIT entry on the bridge's own port. A
        // listener that never accepted anything leaves none, and would prove nothing about a bridge that worked.
        await ServeOneConnectionThenStopAsync(port).ConfigureAwait(false);

        var probeSaysAvailable = ContainerBridgeListenerProbe.IsPortAvailable(IPAddress.Loopback, port);
        var kestrelRebound = await TryStartKestrelAsync(port).ConfigureAwait(false);

        AssertEx.True(kestrelRebound,
            $"A node restarting onto its own bridge port {port} must be able to rebind it; otherwise every node after the first has no bridge.");
        AssertEx.Equal(kestrelRebound, probeSaysAvailable,
            "The probe exists to predict Kestrel's bind. A port Kestrel takes and the probe refuses costs the node its bridge for nothing.");
    }

    [Test]
    [Arguments("http://127.0.0.1:18790", true)]
    [Arguments("https://127.0.0.1:18790", true)]
    [Arguments("http://localhost:18790", true)]
    [Arguments("http://127.0.0.1:5000", false)]
    public void CollidesWithHostingUrls_MatchesOnThePortWhateverTheHostOrScheme(string url, bool expected)
    {
        AssertEx.Equal(expected, ContainerBridgeListenerProbe.CollidesWithHostingUrls([url], port: 18790),
            $"'{url}' decides whether the node's own listener already claims the bridge's port.");
    }

    /// <summary>
    ///     The case the socket probe cannot see: at this point in startup the node has not bound its own listener
    ///     yet, so the port is genuinely free and would stop being free a moment later.
    /// </summary>
    [Test]
    public void CollidesWithHostingUrls_FindsTheClashAmongSeveralUrls()
    {
        AssertEx.True(ContainerBridgeListenerProbe.CollidesWithHostingUrls(["http://127.0.0.1:5000", "http://[::1]:18790"], port: 18790),
            "A desktop launch given the bridge's port puts ordinary SPA and API requests on a port the bridge claims.");
    }

    [Test]
    public void CollidesWithHostingUrls_WithNoUrls_IsFalse()
    {
        AssertEx.False(ContainerBridgeListenerProbe.CollidesWithHostingUrls([], port: 18790));
    }

    /// <summary>Runs a listener on <paramref name="port" />, serves one connection, and closes it — a bridge that a container used, and the node it then stopped.</summary>
    private static async Task ServeOneConnectionThenStopAsync(int port)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(backlog: 1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using (var accepted = await listener.AcceptAsync().ConfigureAwait(false))
        {
            await connecting.ConfigureAwait(false);

            // The listening side closes first, which is what puts ITS local port — the bridge's port — into
            // TIME_WAIT rather than the client's ephemeral one.
            accepted.Close();
        }

        client.Close();
    }

    /// <summary>Whether a real Kestrel host binds <paramref name="port" />, which is the answer the probe has to predict.</summary>
    private static async Task<bool> TryStartKestrelAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
#pragma warning disable S5332 // A loopback listener in a test, matching the bridge's own plain-HTTP shape.
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
#pragma warning restore S5332

        await using var app = builder.Build();
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // What Kestrel reports when the address it was given is not available.
            return false;
        }

        await app.StopAsync().ConfigureAwait(false);
        return true;
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
