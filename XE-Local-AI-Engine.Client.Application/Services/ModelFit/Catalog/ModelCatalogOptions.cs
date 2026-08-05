namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     Operator-configurable remote-refresh settings for the curated model catalog. Bound from the
///     <see cref="SectionName" /> configuration section; every field has a safe default so the catalog works
///     bundled-only with zero configuration.
/// </summary>
public sealed class ModelCatalogOptions
{
    /// <summary>Configuration section name this options type binds from.</summary>
    public const string SectionName = "ModelCatalog";

    /// <summary>Named <see cref="HttpClient" /> the catalog provider resolves via <see cref="IHttpClientFactory" />.</summary>
    public const string HttpClientName = "model-catalog";

    /// <summary>Default remote-refresh cadence when <see cref="RefreshTtl" /> is not overridden.</summary>
    public static readonly TimeSpan DefaultRefreshTtl = TimeSpan.FromHours(hours: 24);

    /// <summary>Default per-fetch timeout when <see cref="FetchTimeout" /> is not overridden.</summary>
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(seconds: 20);

    /// <summary>
    ///     Remote catalog URL to refresh from (e.g. a GitHub raw URL of this repo's seed file). <see langword="null" />
    ///     or empty (the default) means bundled-only — the provider never makes a network call.
    /// </summary>
    public string? RefreshUrl { get; set; }

    /// <summary>How often a stale in-memory catalog is re-fetched from <see cref="RefreshUrl" />.</summary>
    public TimeSpan RefreshTtl { get; set; } = DefaultRefreshTtl;

    /// <summary>Per-fetch HTTP timeout; a fetch that exceeds this falls back to the last-good/bundled chain.</summary>
    public TimeSpan FetchTimeout { get; set; } = DefaultFetchTimeout;
}
