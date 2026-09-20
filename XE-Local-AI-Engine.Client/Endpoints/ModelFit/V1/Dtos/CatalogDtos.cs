namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>
///     Provenance metadata for the curated model catalog in effect (<c>GET model-fit/catalog</c>,
///     <c>POST model-fit/catalog/refresh</c>): which catalog build is active and where it came from.
/// </summary>
/// <remarks>
///     The catalog CONTENT rides the recommendations response instead, in each row's <c>section</c>/<c>tier</c> fields.
/// </remarks>
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

    /// <summary>Whether a remote refresh source is configured at all (<c>ModelCatalog:RefreshUrl</c>).</summary>
    /// <remarks>
    ///     <c>false</c> means bundled-only: <c>POST model-fit/catalog/refresh</c> is a guaranteed no-op that still
    ///     answers 200 with the snapshot in effect (no error to report, nothing fetched), so the UI must not present
    ///     that outcome as a successful refresh. No shipped appsettings file has a <c>ModelCatalog</c> section, so a
    ///     stock node reads <c>false</c>.
    /// </remarks>
    public required bool RefreshSourceConfigured { get; init; }
}
