namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>
///     Provenance of the curated model catalog currently in effect (<c>GET model-fit/catalog</c> /
///     <c>POST model-fit/catalog/refresh</c>). The catalog CONTENT rides the existing recommendations response
///     (each row's <c>section</c>/<c>tier</c> fields) — this DTO is metadata only: which catalog build is active and
///     where it came from.
/// </summary>
public sealed class ModelCatalogInfoResponse
{
    public required string CatalogVersion { get; init; }

    public string? UpdatedAt { get; init; }

    /// <summary><c>bundled</c> / <c>remote</c> / <c>remoteLastGood</c> — see <c>ModelCatalogSource</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Unix-ms instant the served catalog was fetched; <c>null</c> for a bundled (never-fetched) catalog.</summary>
    public long? FetchedAtUtc { get; init; }

    /// <summary>The catalog's configured remote refresh URL, or <c>null</c> when bundled-only (no URL configured).</summary>
    public string? SourceUrl { get; init; }

    public required int ModelCount { get; init; }

    /// <summary>
    ///     Whether a remote refresh source is configured at all (<c>ModelCatalog:RefreshUrl</c>). When <c>false</c>, the
    ///     catalog is bundled-only and <c>POST model-fit/catalog/refresh</c> is a guaranteed no-op — it still returns 200
    ///     with the snapshot in effect, because there is no error to report, but nothing was or could be fetched.
    ///     The UI must not present that outcome as a successful refresh: no appsettings file ships a
    ///     <c>ModelCatalog</c> section, so on a stock node this is <c>false</c> and "Refresh catalog" previously showed a
    ///     green success toast for an action that could never do anything.
    /// </summary>
    public required bool RefreshSourceConfigured { get; init; }
}
