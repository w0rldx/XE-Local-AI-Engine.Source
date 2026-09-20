namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     Serves the curated model catalog the recommendation ranking lane reads: bundled by default, optionally kept
///     fresh from an operator-configured remote URL.
/// </summary>
/// <remarks>
///     A remote-fetch failure never surfaces to the caller — it falls back to the last-good persisted remote catalog,
///     else to the bundled seed.
/// </remarks>
public interface IModelCatalogProvider
{
    /// <summary>
    ///     Returns the currently-effective catalog. When a remote refresh URL is configured and the in-memory copy has
    ///     exceeded its TTL, this attempts one refresh first (see <see cref="RefreshAsync" /> for the fallback chain).
    /// </summary>
    Task<ModelCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Forces an immediate remote-refresh attempt, ignoring the TTL, when a refresh URL is configured; a no-op
    ///     returning the bundled snapshot otherwise.
    /// </summary>
    /// <remarks>
    ///     On fetch or validation failure it falls back to the last-good persisted remote catalog, else to the
    ///     in-memory snapshot already served: a single transient failure never regresses a working remote catalog to
    ///     bundled.
    /// </remarks>
    Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
