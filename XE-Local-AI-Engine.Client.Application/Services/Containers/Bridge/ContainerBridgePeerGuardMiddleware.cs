namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

/// <summary>
///     Refuses any connection to the bridge listener whose peer is not this computer, before anything else on that
///     listener runs.
///     <para>
///         The bridge is the engine's one deliberately non-loopback listener, so <c>LocalApiSecurityMiddleware</c>'s
///         loopback-peer check cannot guard it — that check would reject exactly the traffic the bridge exists to
///         accept. This is the compensating control: the peer must be one of the host's own addresses, which a
///         container on an engine-owned network is (it reaches the bridge through the host) and another machine on
///         the LAN is not.
///     </para>
///     <para>
///         It is inert on any connection that did not arrive on the bridge port, so registering it outside the bridge
///         branch cannot change what the main listener serves.
///     </para>
/// </summary>
public sealed class ContainerBridgePeerGuardMiddleware : IMiddleware
{
    /// <summary>
    ///     The OpenAI-style error envelope this subsystem answers in, rather than RFC 7807: the bridge's callers are
    ///     OpenAI-compatible clients inside containers, and a ProblemDetails body is one they surface as nothing.
    /// </summary>
    internal const string ForbiddenBody =
        """{"error":{"message":"The container bridge accepts connections from this computer only.","type":"forbidden","code":null}}""";

    private readonly ContainerBridgeEndpointSource _endpoints;
    private readonly ILogger<ContainerBridgePeerGuardMiddleware> _logger;
    private readonly ContainerBridgeAddressWatcher _watcher;

    public ContainerBridgePeerGuardMiddleware(ContainerBridgeEndpointSource endpoints,
        ContainerBridgeAddressWatcher watcher,
        ILogger<ContainerBridgePeerGuardMiddleware> logger)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (_endpoints.Current is not { } endpoint
            || !endpoint.Matches(context.Connection.LocalIpAddress, context.Connection.LocalPort))
        {
            // Not a bridge connection. The main listener's own pipeline owns this request and this guard has no
            // opinion about it. The whole local endpoint decides, not the port alone: a node whose loopback listener
            // carries the bridge's port must not have its own requests judged by this guard. (Such a node opens no
            // bridge at all — this is the second half of that answer, and the reason the two must agree.)
            await next(context).ConfigureAwait(false);
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (!_watcher.IsSameHost(remoteAddress))
        {
            if (IsContainerShapedAddress(remoteAddress))
            {
                // The diagnosable case. A refused peer in a private range is what an untranslated container source
                // address looks like, and the one platform that produces it is a rootful Linux daemon: a packet
                // addressed to one of the host's own addresses is routed PREROUTING to INPUT and never passes
                // POSTROUTING, so the daemon's masquerade rule does not rewrite the source. Only a rootless daemon
                // is validated; the recorded remedy is to admit the subnets of networks the engine itself created.
                _logger.LogWarning("The container bridge refused a connection from {RemoteAddress}: it accepts this computer's own addresses only. "
                                   + "That peer is in a private or link-local range, which is how a container's OWN address arrives when the container "
                                   + "daemon did not translate it — the expected symptom on a rootful Linux daemon, where traffic to a local address is "
                                   + "never masqueraded. Only a rootless daemon is validated for this path; see ADR 0011.",
                    remoteAddress);
            }
            else
            {
                _logger.LogWarning("The container bridge refused a connection from {RemoteAddress}: it accepts this computer's own addresses only.", remoteAddress);
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = MediaTypeNames.Application.Json;
            await context.Response.WriteAsync(ForbiddenBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    ///     Whether a refused peer looks like a container's own untranslated address: an RFC 1918 private range or the
    ///     169.254/16 link-local range. It changes nothing about the verdict — the connection is refused either way —
    ///     and exists only so the warning can name the probable cause instead of leaving the next person to capture
    ///     packets to find it.
    /// </summary>
    internal static bool IsContainerShapedAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        if (!address.TryWriteBytes(octets, out var written) || written != 4)
        {
            return false;
        }

        return octets[0] == 10
               || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
               || (octets[0] == 192 && octets[1] == 168)
               || (octets[0] == 169 && octets[1] == 254);
    }
}
