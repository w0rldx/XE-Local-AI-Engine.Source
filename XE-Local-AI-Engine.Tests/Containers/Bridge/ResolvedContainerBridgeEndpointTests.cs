namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Which connections belong to the bridge. One predicate answers it for both callers — the branch in
///     <c>ContainerBridgePipeline</c> and the peer guard's own inertness check — because two definitions of "is this
///     the bridge" that could drift apart is exactly how a request gets judged by one and routed by the other.
///     <para>
///         The port alone is not the answer. A desktop launch given <c>--port 18790</c> puts the node's loopback
///         listener on the bridge's port, and every SPA and API request would be routed into the bridge branch and
///         answered 401 by a token gate that has nothing to do with them. Such a node opens no bridge at all; this
///         is the other half of that answer, and it holds even if the refusal is ever bypassed.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ResolvedContainerBridgeEndpointTests
{
    private const int BridgePort = 18790;

    private static readonly IPAddress BridgeAddress = IPAddress.Parse("172.20.0.1");

    [Test]
    public void Matches_OnTheBridgesOwnAddressAndPort_IsTrue()
    {
        AssertEx.True(Endpoint().Matches(BridgeAddress, BridgePort), "This is the bridge listener's own local endpoint.");
    }

    /// <summary>The P2 case: same port, the node's OWN listener. It is not the bridge and must never be treated as one.</summary>
    [Test]
    public void Matches_OnTheSamePortButTheLoopbackListener_IsFalse()
    {
        AssertEx.False(Endpoint().Matches(IPAddress.Loopback, BridgePort),
            "A loopback listener sharing the bridge's port carries the node's own SPA and API traffic, which must not enter the bridge branch.");
    }

    [Test]
    public void Matches_OnTheBridgeAddressButAnotherPort_IsFalse()
    {
        AssertEx.False(Endpoint().Matches(BridgeAddress, 5000), "A second listener on the same NIC is not the bridge.");
    }

    /// <summary>
    ///     A dual-stack socket reports a bound IPv4 address in its mapped form, and <c>IPAddress.Equals</c> does not
    ///     treat the two spellings as equal — so without folding, a real node's bridge would match nothing.
    /// </summary>
    [Test]
    public void Matches_FoldsAnIPv4MappedIPv6LocalAddressBackToIPv4()
    {
        AssertEx.True(Endpoint().Matches(BridgeAddress.MapToIPv6(), BridgePort),
            "A dual-stack listener reports the mapped spelling; the bridge is still the bridge.");
    }

    [Test]
    public void Matches_WithNoLocalAddress_IsFalse()
    {
        AssertEx.False(Endpoint().Matches(localAddress: null, BridgePort),
            "A connection whose local end cannot be read is not one the branch may claim.");
    }

    /// <summary>
    ///     The branch predicate reads the same fact off a real <see cref="HttpContext" />, so the two cannot disagree
    ///     about a request the pipeline will actually see.
    /// </summary>
    [Test]
    [Arguments("172.20.0.1", BridgePort, true)]
    [Arguments("127.0.0.1", BridgePort, false)]
    [Arguments("172.20.0.1", 5000, false)]
    public void IsBridgeConnection_ReadsTheWholeLocalEndpointOffTheConnection(string localAddress, int localPort, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Parse(localAddress);
        context.Connection.LocalPort = localPort;

        AssertEx.Equal(expected, ContainerBridgePipeline.IsBridgeConnection(context, Endpoint()),
            $"A connection accepted on {localAddress}:{localPort} decides whether the bridge branch claims it.");
    }

    private static ResolvedContainerBridgeEndpoint Endpoint()
    {
        return new ResolvedContainerBridgeEndpoint(BridgeAddress, BridgePort, $"{BridgeAddress}:{BridgePort}");
    }
}
