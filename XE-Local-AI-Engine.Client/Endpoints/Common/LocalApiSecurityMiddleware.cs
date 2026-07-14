namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using System.Net;

/// <summary>
///     Local API contract type for local api security middleware.
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
