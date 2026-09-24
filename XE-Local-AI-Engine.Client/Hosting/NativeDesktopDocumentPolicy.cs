namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>Restricts only documents requested by the GTK desktop WebView.</summary>
internal sealed class NativeDesktopDocumentPolicy
{
    internal const string UserAgentMarker = "XE-Native-Restricted/1";
    internal const string ContentPolicy = "frame-src 'none'; object-src 'none'";
    internal const string PermissionsPolicy = "microphone=(self), camera=(), display-capture=()";

    private readonly RequestDelegate _next;

    public NativeDesktopDocumentPolicy(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            Apply((HttpContext)state);
            return Task.CompletedTask;
        }, context);
        return _next(context);
    }

    private static void Apply(HttpContext context)
    {
        var response = context.Response;
        if (!string.Equals(response.ContentType?.Split(';', 2)[0].Trim(), "text/html", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        response.Headers.Append("Vary", "User-Agent");
        var userAgent = context.Request.Headers.UserAgent.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!userAgent.Contains(UserAgentMarker, StringComparer.Ordinal))
        {
            return;
        }

        response.Headers.Append("Content-Security-Policy", ContentPolicy);
        if (!response.Headers.ContainsKey("Permissions-Policy"))
        {
            response.Headers["Permissions-Policy"] = PermissionsPolicy;
        }

        response.Headers.CacheControl = "no-store";
    }
}
