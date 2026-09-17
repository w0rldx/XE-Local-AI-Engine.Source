namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The compensating control for the engine's one deliberately non-loopback listener. <c>LocalApiSecurityMiddleware</c>
///     cannot guard the bridge — its loopback-peer check would reject exactly the traffic the bridge exists to accept —
///     so this asserts the replacement is at least as strict about who gets through.
///     <para>
///         Driven directly rather than through the in-memory TestServer, which presents a null peer and so cannot
///         express the one input this middleware decides on.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerBridgePeerGuardMiddlewareTests
{
    private const int BridgePort = 18790;

    /// <summary>The address the bridge listener is bound to in these tests; a connection's local end must match it to be judged at all.</summary>
    private static readonly IPAddress BridgeBindAddress = IPAddress.Parse("172.18.0.1");

    [Test]
    public async Task Bridge_WhenThePeerIsThisComputer_IsAllowedThrough()
    {
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Parse("172.18.0.1"));
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.True(nextCalled, "A container reaching the bridge through one of this host's own addresses must pass.");
    }

    [Test]
    public async Task Bridge_WhenThePeerIsLoopback_IsAllowedThrough()
    {
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Loopback);
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.True(nextCalled, "The engine's own calls to its bridge arrive over loopback.");
    }

    [Test]
    [Arguments("203.0.113.7")]
    [Arguments("192.168.1.11")]
    [Arguments("2001:db8::99")]
    [Arguments("::ffff:203.0.113.7")]
    public async Task Bridge_WhenThePeerIsAnotherMachine_IsRefusedWithTheOpenAiErrorEnvelope(string remote)
    {
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Parse(remote));
        var body = new MemoryStream();
        context.Response.Body = body;
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        AssertEx.False(nextCalled, $"A peer at {remote} is not this computer and must never reach anything behind the guard.");
        AssertEx.Equal(ContainerBridgePeerGuardMiddleware.ForbiddenBody, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    ///     The refusal is identical either way — this is about the operator's ability to diagnose it. A refused peer
    ///     in a private range is a container's own untranslated source address, which is the expected symptom on a
    ///     rootful Linux daemon; without naming it, the only route to that answer is a packet capture, which is how
    ///     it was found the first time.
    /// </summary>
    [Test]
    [Arguments("172.20.0.5")]
    [Arguments("10.1.2.3")]
    [Arguments("192.168.7.7")]
    [Arguments("169.254.10.10")]
    public async Task Bridge_WhenTheRefusedPeerLooksLikeAContainer_NamesTheProbableCause(string remote)
    {
        var logger = new RecordingLogger<ContainerBridgePeerGuardMiddleware>();
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Parse(remote));
        context.Response.Body = new MemoryStream();

        using var watcher = CreateWatcher();
        await new ContainerBridgePeerGuardMiddleware(Endpoints(), watcher, logger)
              .InvokeAsync(context, static _ => Task.CompletedTask);

        AssertEx.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode, "The diagnosis must not change the verdict.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "rootful"),
            $"A refusal of the private address {remote} must name the rootful-daemon hypothesis, or the next person repeats the packet capture.");
    }

    /// <summary>A public peer is another machine, and saying "probably a rootful daemon" about it would be a wrong diagnosis, not a helpful one.</summary>
    [Test]
    public async Task Bridge_WhenTheRefusedPeerIsPublic_DoesNotBlameTheDaemon()
    {
        var logger = new RecordingLogger<ContainerBridgePeerGuardMiddleware>();
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Parse("203.0.113.7"));
        context.Response.Body = new MemoryStream();

        using var watcher = CreateWatcher();
        await new ContainerBridgePeerGuardMiddleware(Endpoints(), watcher, logger)
              .InvokeAsync(context, static _ => Task.CompletedTask);

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "accepts this computer's own addresses only"), "The refusal is still reported.");
        AssertEx.False(logger.HasEntry(LogLevel.Warning, "rootful"), "A routable public peer is another machine; naming the daemon would be a wrong diagnosis.");
    }

    [Test]
    [Arguments("203.0.113.7", false)]
    [Arguments("172.15.0.1", false)]
    [Arguments("172.32.0.1", false)]
    [Arguments("2001:db8::99", false)]
    [Arguments("172.16.0.1", true)]
    [Arguments("172.31.255.254", true)]
    [Arguments("10.0.0.1", true)]
    [Arguments("192.168.0.1", true)]
    [Arguments("169.254.1.1", true)]
    public void ContainerShapedAddress_MatchesTheRfc1918AndLinkLocalRangesAndNothingElse(string address, bool expected)
    {
        AssertEx.Equal(expected,
            ContainerBridgePeerGuardMiddleware.IsContainerShapedAddress(IPAddress.Parse(address)),
            $"'{address}' decides whether the refusal warning names the rootful hypothesis.");
    }

    [Test]
    public async Task Bridge_WhenThereIsNoPeerAddress_IsRefused()
    {
        var context = CreateContext(localPort: BridgePort, remoteIp: null);
        context.Response.Body = new MemoryStream();
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        AssertEx.False(nextCalled, "A connection with no peer address is not one the bridge can vouch for.");
    }

    /// <summary>
    ///     The guard is inert off the bridge listener, so registering it anywhere else cannot change what the main
    ///     listener serves — including for a peer it would have refused on the bridge port.
    /// </summary>
    [Test]
    public async Task MainListener_IsUntouchedByTheGuard_EvenForAPeerTheBridgeWouldRefuse()
    {
        var context = CreateContext(localPort: 5000, remoteIp: IPAddress.Parse("203.0.113.7"));
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.True(nextCalled, "A request that did not arrive on the bridge port belongs to the main pipeline, which owns its own peer checks.");
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    ///     The port alone is not the bridge. A node whose loopback listener carries the bridge's port would
    ///     otherwise have every one of its own requests judged by this guard, and a peer the guard dislikes would be
    ///     answered 403 on the node's own UI. Such a node opens no bridge at all; this is the other half of that
    ///     answer, and it must hold on its own.
    /// </summary>
    [Test]
    public async Task MainListener_OnTheBridgesPortButItsOwnAddress_IsStillUntouchedByTheGuard()
    {
        var context = CreateContext(IPAddress.Loopback, BridgePort, IPAddress.Parse("203.0.113.7"));
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await CreateMiddleware(watcher).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        AssertEx.True(nextCalled, "A connection accepted on the node's own listener is the node's own, whatever port it shares with the bridge.");
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>A node that opened no bridge has no listener for this guard to police, so it must stay out of the way rather than refuse.</summary>
    [Test]
    public async Task WithNoBridgeOnThisNode_TheGuardIsInert()
    {
        var context = CreateContext(localPort: BridgePort, remoteIp: IPAddress.Parse("203.0.113.7"));
        var nextCalled = false;

        using var watcher = CreateWatcher();
        await new ContainerBridgePeerGuardMiddleware(new ContainerBridgeEndpointSource(endpoint: null),
                  watcher,
                  NullLogger<ContainerBridgePeerGuardMiddleware>.Instance)
              .InvokeAsync(context, _ =>
              {
                  nextCalled = true;
                  return Task.CompletedTask;
              });

        AssertEx.True(nextCalled, "With no resolved bridge there is no bridge connection to refuse, and the node's own pipeline owns the request.");
    }

    private static ContainerBridgeAddressWatcher CreateWatcher()
    {
        return new ContainerBridgeAddressWatcher(BridgeOptions(),
            new ManualTimeProvider(),
            NullLogger<ContainerBridgeAddressWatcher>.Instance,
            static () => [IPAddress.Parse("172.18.0.1"), IPAddress.Parse("192.168.1.10")]);
    }

    private static ContainerBridgePeerGuardMiddleware CreateMiddleware(ContainerBridgeAddressWatcher watcher)
    {
        return new ContainerBridgePeerGuardMiddleware(Endpoints(), watcher, NullLogger<ContainerBridgePeerGuardMiddleware>.Instance);
    }

    private static IOptions<ContainerBridgeOptions> BridgeOptions()
    {
        return Options.Create(new ContainerBridgeOptions
        {
            Enabled = true,
            Port = BridgePort
        });
    }

    /// <summary>
    ///     The guard decides inertness off the RESOLVED listener, not off the configured port, so the fixture has to
    ///     carry one: the whole local endpoint is what separates the bridge from a node listener that happens to
    ///     share its port.
    /// </summary>
    private static ContainerBridgeEndpointSource Endpoints()
    {
        return new ContainerBridgeEndpointSource(new ResolvedContainerBridgeEndpoint(BridgeBindAddress, BridgePort, $"{BridgeBindAddress}:{BridgePort}"));
    }

    private static DefaultHttpContext CreateContext(int localPort, IPAddress? remoteIp)
    {
        return CreateContext(BridgeBindAddress, localPort, remoteIp);
    }

    private static DefaultHttpContext CreateContext(IPAddress localAddress, int localPort, IPAddress? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = localAddress;
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = remoteIp;
        context.Request.Path = "/llm/v1/chat/completions";
        return context;
    }
}
