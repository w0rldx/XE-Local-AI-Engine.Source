namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics;
using Microsoft.Net.Http.Headers;

/// <summary>The response-wide browser-hardening and trace-correlation headers both the real host and the vault pre-host send.</summary>
internal static class NodeResponseHeaders
{
    /// <summary>
    ///     Applies <c>X-Frame-Options: DENY</c>, <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: same-origin</c>
    ///     and <c>traceresponse</c> through <c>OnStarting</c>, so even a short-circuit response carries them. An existing
    ///     trace header is never overwritten.
    /// </summary>
    internal static IApplicationBuilder UseNodeResponseHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            var activity = Activity.Current;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderNames.XFrameOptions] = "DENY";
                context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                // Not no-referrer: under it a browser sends "Origin: null" on a same-origin POST, which the loopback gate refuses.
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                if (activity is not null && !context.Response.Headers.ContainsKey(TraceResponseHeader.HeaderName))
                {
                    // The trace-flags byte reflects the activity's actual recorded state rather than a hardcoded "01"
                    // (see TraceResponseHeader.Build), so a downstream reader is not told the span was sampled when it was not.
                    context.Response.Headers[TraceResponseHeader.HeaderName] = TraceResponseHeader.Build(activity);
                }

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
