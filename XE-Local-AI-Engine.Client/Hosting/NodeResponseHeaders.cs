namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics;
using Microsoft.Net.Http.Headers;

/// <summary>The response-wide anti-framing and trace-correlation headers both the real host and the vault pre-host send.</summary>
internal static class NodeResponseHeaders
{
    /// <summary>
    ///     Applies <c>X-Frame-Options: DENY</c> and the <c>traceresponse</c> header through <c>OnStarting</c>, so even a
    ///     short-circuit response carries the anti-framing defense. An existing trace header is never overwritten.
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
