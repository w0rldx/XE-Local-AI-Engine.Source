namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Serves the curated External Apps catalog: bundled by default, optionally kept fresh from an
///     operator-configured remote URL. Fail-closed — the served snapshot has always passed
///     <see cref="ExternalAppCatalogValidator.Validate" />, and a fetch failure falls back to the last-good persisted
///     copy, else the bundled seed, rather than surfacing to the caller.
/// </summary>
public interface IApplicationCatalogProvider
{
    /// <summary>
    ///     Returns the currently-effective catalog. When a refresh URL is configured and the in-memory copy has
    ///     exceeded its TTL, this attempts one refresh first (see <see cref="RefreshAsync" /> for the fallback chain).
    /// </summary>
    Task<ExternalAppCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the manifest with <paramref name="applicationId" /> from the currently-effective catalog, or
    ///     <see langword="null" /> when the catalog declares no such application. One filter over the snapshot, so
    ///     the install flow and the per-application endpoint do not each re-implement the lookup.
    /// </summary>
    Task<ApplicationManifest?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Forces an immediate refresh attempt, ignoring the TTL, when a refresh URL is configured; a no-op returning
    ///     the current snapshot otherwise. The result carries the snapshot now being served plus a human-readable
    ///     <see cref="ExternalAppCatalogRefreshResult.FailureMessage" /> when the attempt failed and the provider fell
    ///     back; the same string is carried on <see cref="ExternalAppCatalogSnapshot.LastRefreshFailure" /> so a later
    ///     read reports it without re-attempting a fetch.
    /// </summary>
    Task<ExternalAppCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
}
