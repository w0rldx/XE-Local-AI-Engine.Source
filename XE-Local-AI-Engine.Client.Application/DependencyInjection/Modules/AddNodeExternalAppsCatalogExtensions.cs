namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

internal static class AddNodeExternalAppsCatalogExtensions
{
    /// <summary>
    ///     Registers the curated External Apps catalog: its options, the named refresh client, the cache store and the
    ///     provider every catalog read goes through.
    /// </summary>
    /// <remarks>
    ///     <c>ApplicationCatalogProvider.ReadCappedAsync</c> is the document cap that actually holds:
    ///     <c>MaxResponseContentBufferSize</c> below is inert, because the provider reads with
    ///     <c>HttpCompletionOption.ResponseHeadersRead</c>, and stands only as a belt for a future buffered read. The
    ///     provider's first <c>GetCatalogAsync</c> is the first catalog request, so a disabled build makes no network call.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeExternalAppsCatalog(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Curated External Apps catalog: a bundled JSON seed plus an optional operator-configured remote refresh. RefreshUrl ships
        // EMPTY (never a network call); a value is accepted only as https or http to a loopback host, and a rejected one logs once.
        builder.Services.Configure<ExternalAppCatalogOptions>(configuration.GetSection(ExternalAppCatalogOptions.SectionName));
        // A named client via IHttpClientFactory, never a bare HttpClient, so every consumer stays test-factory-safe by construction.
        // AllowAutoRedirect is OFF against the default: a 3xx must be a fetch failure, or the loopback-http allowance could be bounced to a public plain-http host.
        builder.Services.AddHttpClient(ExternalAppCatalogOptions.HttpClientName)
               .ConfigureHttpClient(static client =>
               {
                   client.MaxResponseContentBufferSize = ExternalAppCatalogOptions.DefaultMaxDocumentBytes;
                   client.DefaultRequestHeaders.UserAgent.ParseAdd("XE-Local-AI-Engine-ExternalApps/1.0");
               })
               .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
               {
                   AllowAutoRedirect = false
               });
        // The cache store persists a tiny node-local JSON file under external-apps/; the provider owns the bundled/remote/last-good
        // snapshot and TTL-gated refresh. Both singletons, neither on the startup path: the ExternalApps:Enabled kill switch 404s the first catalog request. Add no eager consumer.
        builder.Services.AddSingleton<IExternalAppCatalogCacheStore, ExternalAppCatalogCacheStore>();
        builder.Services.AddSingleton<IApplicationCatalogProvider, ApplicationCatalogProvider>();

        return builder;
    }
}
