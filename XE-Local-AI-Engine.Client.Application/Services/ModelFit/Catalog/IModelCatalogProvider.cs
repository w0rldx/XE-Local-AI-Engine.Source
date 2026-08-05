namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     Serves the curated model catalog the recommendation ranking lane reads: bundled by default, optionally kept
///     fresh from an operator-configured remote URL. A remote-fetch failure never surfaces to the
///     caller — it silently falls back to the last-good persisted remote catalog, else the bundled seed.
/// </summary>
public interface IModelCatalogProvider
{
    /// <summary>
    ///     Returns the currently-effective catalog. When a remote refresh URL is configured and the in-memory copy has
    ///     exceeded its TTL, this attempts one refresh first (see <see cref="RefreshAsync" /> for the fallback chain).
    /// </summary>
    Task<ModelCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Forces an immediate remote-refresh attempt (ignoring the TTL) when a refresh URL is configured; a no-op
    ///     returning the bundled snapshot otherwise. On fetch/validation failure, falls back to the last-good persisted
    ///     remote catalog, else the in-memory snapshot already served (never regresses a working remote catalog to
    ///     bundled on a single transient failure).
    /// </summary>
    Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
