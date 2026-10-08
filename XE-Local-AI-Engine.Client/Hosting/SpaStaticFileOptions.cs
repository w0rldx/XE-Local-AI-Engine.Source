namespace XE_Local_AI_Engine.Client.Hosting;

using Microsoft.AspNetCore.StaticFiles;

/// <summary>
///     SPA cache policy: the HTML shell always revalidates, so a browser never keeps an upgraded-away shell that points
///     at deleted entry chunks; Vite's content-hashed <c>/assets</c> files are immutable.
/// </summary>
internal static class SpaStaticFileOptions
{
    internal const string ShellCacheControl = "no-cache";
    internal const string HashedAssetCacheControl = "public, max-age=31536000, immutable";

    public static StaticFileOptions Create()
    {
        return new StaticFileOptions
        {
            OnPrepareResponse = Apply
        };
    }

    private static void Apply(StaticFileResponseContext context)
    {
        // The HTML check comes first: the SPA fallback answers an unknown /assets URL with index.html, which must never be marked immutable.
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = ShellCacheControl;
        }
        else if (context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = HashedAssetCacheControl;
        }
    }
}
