namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

internal static class AddNodeExternalAppsCatalogExtensions
{
    public static IHostApplicationBuilder AddNodeExternalAppsCatalog(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Curated External Apps catalog: a bundled JSON seed plus an optional operator-configured remote refresh.
        // RefreshUrl ships EMPTY (bundled-only, never a network call); a configured value is accepted only when it is
        // https, or http to a loopback host, and a rejected value logs once and leaves the node bundled-only.
        builder.Services.Configure<ExternalAppCatalogOptions>(configuration.GetSection(ExternalAppCatalogOptions.SectionName));
        // The named HttpClient is resolved via IHttpClientFactory, never injected as a bare HttpClient, so every
        // consumer stays test-factory-safe by construction. ApplicationCatalogProvider.ReadCappedAsync is the document
        // cap that actually holds: MaxResponseContentBufferSize below is inert because the provider reads with
        // HttpCompletionOption.ResponseHeadersRead, so it is a belt for any future buffered read and nothing more.
        // AllowAutoRedirect is turned OFF explicitly because the
        // default is on: a 3xx must be a fetch failure, or the loopback-http allowance could be bounced to a public
        // plain-http host.
        builder.Services.AddHttpClient(ExternalAppCatalogOptions.HttpClientName)
               .ConfigureHttpClient(static client =>
               {
                   client.MaxResponseContentBufferSize = ExternalAppCatalogOptions.DefaultMaxDocumentBytes;
                   client.DefaultRequestHeaders.UserAgent.ParseAdd("XE-Local-AI-Engine-ExternalApps/1.0");
               })
               .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });
        // The cache store persists a tiny node-local JSON file under external-apps/; the provider owns the in-memory
        // bundled/remote/last-good snapshot plus TTL-gated refresh serialization. Both singletons, and neither is on
        // the startup path: the provider's first GetCatalogAsync is the first catalog request, which the
        // ExternalApps:Enabled kill switch 404s while the feature is off — so a disabled build makes no network call.
        // Do not add an eager consumer.
        builder.Services.AddSingleton<IExternalAppCatalogCacheStore, ExternalAppCatalogCacheStore>();
        builder.Services.AddSingleton<IApplicationCatalogProvider, ApplicationCatalogProvider>();

        return builder;
    }
}
