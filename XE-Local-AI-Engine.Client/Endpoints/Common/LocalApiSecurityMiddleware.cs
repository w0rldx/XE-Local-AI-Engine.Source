namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using System.Net;

/// <summary>
///     Guards the loopback-only <c>/api/local/v1</c> surface: a request is served only when its transport peer is a
///     loopback address AND its Host/Origin resolve to a loopback name on the bound port.
/// </summary>
/// <remarks>
///     This backstops the anonymous first-run setup endpoint against a routable caller with a forged Host/Origin. The
///     peer check reads <see cref="Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress" />, the socket peer, so a
///     reverse proxy on the SAME host defeats it; that is acceptable because a proxied or headless deployment is
///     UNSUPPORTED. See docs/wiki/09-api-and-hubs.md ("Security middleware &amp; auth ordering") for why the socket
///     peer is always the real client on every supported launch.
/// </remarks>
public sealed class LocalApiSecurityMiddleware
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1"
    };

    private readonly RequestDelegate _next;

    public LocalApiSecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsLocalApiRequest(context.Request.Path)
            && (!IsLoopbackPeer(context.Connection.RemoteIpAddress)
                || !IsAllowedHost(context.Request.Host.Host)
                || !IsAllowedOrigin(context.Request)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    private static bool IsLoopbackPeer(IPAddress? remoteIpAddress)
    {
        // The transport peer is the authoritative gate: a routable peer must never reach this loopback-only surface even with a forged Host/Origin.
        // A null address means the request never traversed the network stack (in-memory TestServer transport, in-process health probes), so it counts as loopback.
        return remoteIpAddress is null || IPAddress.IsLoopback(remoteIpAddress);
    }

    private static bool IsLocalApiRequest(PathString path)
    {
        return path.StartsWithSegments("/api/local/v1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedHost(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return normalizedHost is not null && AllowedHosts.Contains(normalizedHost);
    }

    private static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        var originHost = NormalizeHost(originUri.Host);
        var requestHost = NormalizeHost(request.Host.Host);
        if (originHost is null || requestHost is null || !AllowedHosts.Contains(originHost))
        {
            return false;
        }

        return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(originHost, requestHost, StringComparison.OrdinalIgnoreCase)
               && ResolvePort(originUri) == ResolvePort(request);
    }

    private static int ResolvePort(Uri originUri)
    {
        if (!originUri.IsDefaultPort)
        {
            return originUri.Port;
        }

        return string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static int ResolvePort(HttpRequest request)
    {
        if (request.Host.Port is { } port)
        {
            return port;
        }

        return string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static string? NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host)
            ? null
            : host.Trim().TrimStart('[').TrimEnd(']');
    }
}
