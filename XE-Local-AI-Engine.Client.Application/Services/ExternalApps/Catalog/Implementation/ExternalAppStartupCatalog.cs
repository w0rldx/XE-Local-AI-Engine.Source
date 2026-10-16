namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Whether the catalog lists an application at startup, read before the container exists so the host can decide whether External Apps start.</summary>
/// <remarks>
///     Rc.3 decision D6: no External Apps surface, the container bridge included, while the catalog is empty. It reads
///     the two documents the provider can serve before its first fetch: the bundled seed, and the persisted last-good
///     copy for the configured refresh URL. Silent, because the provider logs the same problems at its first use.
/// </remarks>
internal static class ExternalAppStartupCatalog
{
    public static bool ListsApplications(string dataDirectory, string? configuredRefreshUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        if (ExternalAppCatalogBundledLoader.Load(NullLogger.Instance).Applications.Count > 0)
        {
            return true;
        }

        if (ApplicationCatalogProvider.ResolveRefreshUrl(configuredRefreshUrl, NullLogger.Instance) is not { } refreshUrl
            || ExternalAppCatalogCacheStore.LoadAtStartup(dataDirectory) is not { } stored
            || !string.Equals(stored.SourceUrl, refreshUrl, StringComparison.Ordinal))
        {
            return false;
        }

        return ExternalAppCatalogValidator.Validate(stored.RawJson).Document is { Applications.Count: > 0 };
    }
}
