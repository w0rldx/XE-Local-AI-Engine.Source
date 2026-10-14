namespace XE_Local_AI_Engine.Client.Hosting;

using Microsoft.Net.Http.Headers;

/// <summary>
///     Refuses every new request once the host is stopping, so a browser tab cannot open a hub connection that the
///     graceful shutdown then waits on.
/// </summary>
/// <remarks>
///     SignalR closes only the connections it holds when <c>ApplicationStopping</c> fires. A WebSocket upgraded after
///     that is in no closing set, and the host waits for it until <c>HostOptions.ShutdownTimeout</c>. The answer is
///     <c>503</c> with <c>Connection: close</c> and a short <c>Retry-After</c>, and the rest of the pipeline never runs.
/// </remarks>
internal static class ShutdownRequestGuard
{
    internal const string RetryAfterSeconds = "2";

    /// <summary>
    ///     Writes the refusal and answers <see langword="true" /> when <paramref name="applicationStopping" /> is
    ///     cancelled; otherwise leaves the response untouched and answers <see langword="false" />.
    /// </summary>
    internal static bool TryRefuse(HttpContext context, CancellationToken applicationStopping)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!applicationStopping.IsCancellationRequested)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers[HeaderNames.Connection] = "close";
        context.Response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds;
        return true;
    }

    /// <summary>Registers the guard; it belongs first in the pipeline, ahead of logging, authentication and routing.</summary>
    internal static IApplicationBuilder UseShutdownRequestGuard(this IApplicationBuilder app, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(lifetime);
        var applicationStopping = lifetime.ApplicationStopping;
        return app.Use((context, next) => TryRefuse(context, applicationStopping) ? Task.CompletedTask : next(context));
    }
}
