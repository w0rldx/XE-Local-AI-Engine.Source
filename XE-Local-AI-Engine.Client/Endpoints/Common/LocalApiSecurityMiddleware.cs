namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using System.Net;

/// <summary>
///     Guards the loopback-only <c>/api/local/v1</c> surface: a request is served only when its transport peer is a
///     loopback address AND its Host/Origin resolve to a loopback name on the bound port. This backstops the anonymous
///     first-run setup endpoint against a routable caller with a forged Host/Origin.
///     <para>
///         The peer check reads <see cref="Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress" />, which is the
///         address of the socket peer — the machine that opened the TCP connection to Kestrel. A reverse proxy running
///         on the SAME host would therefore appear as a loopback peer and defeat this check, since every forwarded
///         request would arrive from 127.0.0.1. That is by design and acceptable because a proxied / headless deployment
///         is UNSUPPORTED: the app binds loopback-only (<c>LoopbackBindGuard</c> shuts the process down on a routable
///         bind), and no forwarded-headers middleware is registered, so <c>X-Forwarded-For</c> is never honoured and the
///         socket peer is always the real client on every supported (same-machine, single-user) launch. Deployments that
///         put this surface behind a proxy or expose it beyond the local machine are out of scope for this guard.
///     </para>
/// </summary>
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

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsLoopbackPeer(IPAddress? remoteIpAddress)
    {
        // The local API is a loopback-only surface: a routable peer must never reach it even with a forged Host/Origin,
        // so the transport-level peer address is the authoritative gate. A null RemoteIpAddress means the request never
        // traversed the network stack — the in-memory TestServer transport and in-process health probes present no peer
        // address — so it is treated as trusted (loopback-equivalent). Only a concrete non-loopback address is rejected.
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
