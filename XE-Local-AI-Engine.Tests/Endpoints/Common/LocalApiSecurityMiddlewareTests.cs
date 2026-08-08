namespace XE_Local_AI_Engine.Tests.Endpoints.Common;

using System.Net;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the loopback-only enforcement added to <see cref="LocalApiSecurityMiddleware" />. The
///     integration surface (host/origin) is covered by <c>LocalApiSecurityTests</c>; these drive the middleware directly
///     so the transport peer address can be set — something the in-memory TestServer cannot express (it presents a null
///     peer).
/// </summary>
public sealed class LocalApiSecurityMiddlewareTests
{
    [Test]
    public async Task LocalApi_WhenPeerIsNonLoopback_IsRejected_EvenWithForgedHostAndNoOrigin()
    {
        var context = CreateLocalApiContext(remoteIp: IPAddress.Parse("203.0.113.7"), host: "localhost");
        var nextCalled = false;
        var middleware = new LocalApiSecurityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        AssertEx.False(nextCalled, "A non-loopback peer must never reach the pipeline behind the middleware.");
    }

    [Test]
    public async Task LocalApi_WhenPeerIsLoopback_IsAllowed()
    {
        var context = CreateLocalApiContext(remoteIp: IPAddress.Loopback, host: "localhost");
        var nextCalled = false;
        var middleware = new LocalApiSecurityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        AssertEx.True(nextCalled, "A loopback peer with an allowed host must pass.");
    }

    [Test]
    public async Task LocalApi_WhenPeerAddressIsNull_IsAllowed()
    {
        // Null RemoteIpAddress models the in-memory TestServer transport / in-process probes: no network peer, so trusted.
        var context = CreateLocalApiContext(remoteIp: null, host: "localhost");
        var nextCalled = false;
        var middleware = new LocalApiSecurityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        AssertEx.True(nextCalled, "A null (in-process) peer must be treated as loopback-equivalent.");
    }

    [Test]
    public async Task NonLocalApiPath_WithNonLoopbackPeer_IsNotGated()
    {
        // The middleware only guards /api/local/v1; the SPA and health endpoints are served to whatever bound the port.
        var context = CreateLocalApiContext(remoteIp: IPAddress.Parse("203.0.113.7"), host: "localhost", path: "/health/live");
        var nextCalled = false;
        var middleware = new LocalApiSecurityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        AssertEx.True(nextCalled, "Only the local API surface is peer-gated.");
    }

    private static DefaultHttpContext CreateLocalApiContext(IPAddress? remoteIp, string host, string path = "/api/local/v1/diagnostics/validation-probe")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        return context;
    }
}
