namespace XE_Local_AI_Engine.Client.Endpoints.Common;

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
            && (!IsAllowedHost(context.Request.Host.Host) || !IsAllowedOrigin(context.Request)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context).ConfigureAwait(false);
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
