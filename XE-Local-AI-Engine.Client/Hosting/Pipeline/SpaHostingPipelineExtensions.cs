namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     The SPA shell's two pipeline pieces: the post-routing guard that keeps the shell fallback off the local API
///     and missing bundle files at 404, and the anonymous <c>index.html</c> fallback itself.
/// </summary>
internal static class SpaHostingPipelineExtensions
{
    /// <summary>
    ///     Marks the SPA shell fallback endpoint so the middleware just after <c>UseRouting</c> can recognise it
    ///     and detach it from requests under the local API prefix. A marker rather than a second routed fallback:
    ///     see the comment at that middleware.
    /// </summary>
    private sealed class SpaFallbackMarker;

    internal static WebApplication UseSpaFallbackApiGuard(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The SPA shell must not answer for the local API: detaching the selected endpoint here, rather than mapping a second fallback over the prefix, is
        // deliberate and is pinned by ValidateExecutableEndpointTests. See docs/wiki/09-api-and-hubs.md ("Static SPA fallback").
        var localApiPrefixPath = new PathString($"/{LocalApiRoutes.Prefix}");
        var spaAssetsPath = new PathString("/assets");
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<SpaFallbackMarker>() is not null
                && context.Request.Path.StartsWithSegments(localApiPrefixPath, StringComparison.OrdinalIgnoreCase))
            {
                context.SetEndpoint(endpoint: null);
            }

            // A bundle file static files did not serve is missing, not unauthorized: answer 404 before the FallbackPolicy
            // challenges the unmatched request. A route mapped under /assets still has its endpoint and passes.
            if (context.GetEndpoint() is null && context.Request.Path.StartsWithSegments(spaAssetsPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        return app;
    }

    internal static WebApplication MapSpaFallback(this WebApplication app, StaticFileOptions spaFiles)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The login page has to load before anyone can hold a token, so the SPA shell opts out of the FallbackPolicy explicitly; static assets are served by
        // UseStaticFiles ahead of routing and are unaffected. The marker is what the middleware just after UseRouting matches on to keep the shell off the API prefix.
        app.MapFallbackToFile("index.html", spaFiles).AllowAnonymous().WithMetadata(new SpaFallbackMarker());

        return app;
    }
}
