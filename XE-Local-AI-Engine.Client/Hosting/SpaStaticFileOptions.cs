namespace XE_Local_AI_Engine.Client.Hosting;

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

/// <summary>
///     SPA cache policy: every non-hashed file (the HTML shell, icons, manifests) revalidates against a content-derived
///     ETag, so an update is never answered with 304; Vite's content-hashed <c>/assets</c> files are immutable.
/// </summary>
internal static class SpaStaticFileOptions
{
    internal const string ShellCacheControl = "no-cache";
    internal const string HashedAssetCacheControl = "public, max-age=31536000, immutable";

    public static StaticFileOptions Create(IFileProvider webRoot)
    {
        return new StaticFileOptions
        {
            FileProvider = new ContentValidatedFileProvider(webRoot),
            OnPrepareResponse = Apply
        };
    }

    /// <summary>Static files with these options, behind a step that drops date validators for non-hashed files.</summary>
    /// <remarks>
    ///     The middleware compares If-Modified-Since, If-Unmodified-Since and a date-form If-Range with the provider's
    ///     hash-derived date before any callback runs, so a client holding an older real date could get a wrong 304. The
    ///     fallback runs later in the same request and sees the stripped headers too. Entity-tag validators stay.
    /// </remarks>
    public static IApplicationBuilder UseContentValidatedStaticFiles(this IApplicationBuilder app, StaticFileOptions options)
    {
        app.Use(static (context, next) =>
        {
            IgnoreDateValidators(context.Request);
            return next(context);
        });
        return app.UseStaticFiles(options);
    }

    private static void IgnoreDateValidators(HttpRequest request)
    {
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || ContentValidatedFileProvider.IsHashedAsset(request.Path.Value))
        {
            return;
        }

        var headers = request.Headers;
        headers.Remove(HeaderNames.IfModifiedSince);
        headers.Remove(HeaderNames.IfUnmodifiedSince);
        var ifRange = headers.IfRange.ToString();
        if (ifRange.Length > 0 && !ifRange.StartsWith('"') && !ifRange.StartsWith("W/", StringComparison.Ordinal))
        {
            // Without its validator the range would be unconditional and could splice a changed file; RFC 9110 sends the whole file.
            headers.Remove(HeaderNames.IfRange);
            headers.Remove(HeaderNames.Range);
        }
    }

    private static void Apply(StaticFileResponseContext context)
    {
        // The request path, not the file name: the SPA fallback rewrites it to /index.html before this runs.
        var headers = context.Context.Response.Headers;
        if (ContentValidatedFileProvider.IsHashedAsset(context.Context.Request.Path.Value))
        {
            headers.CacheControl = HashedAssetCacheControl;
            return;
        }

        // The provider's Last-Modified is a hash, not a date (it can lie in the future); the content ETag is the only validator sent.
        headers.CacheControl = ShellCacheControl;
        headers.Remove(HeaderNames.LastModified);
    }
}
