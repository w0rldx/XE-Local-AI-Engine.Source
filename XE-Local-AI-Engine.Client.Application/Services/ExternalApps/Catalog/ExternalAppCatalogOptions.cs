namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Operator-configurable remote-refresh settings for the curated External Apps catalog. Bound from the
///     <see cref="SectionName" /> configuration section; every field has a safe default so the catalog works
///     bundled-only with zero configuration.
/// </summary>
public sealed class ExternalAppCatalogOptions
{
    /// <summary>Configuration section name this options type binds from.</summary>
    public const string SectionName = "ExternalApps:Catalog";

    /// <summary>Named <see cref="HttpClient" /> the catalog provider resolves via <see cref="IHttpClientFactory" />.</summary>
    public const string HttpClientName = "external-apps-catalog";

    /// <summary>Default per-fetch response cap; a larger document is a fetch failure rather than a buffered payload.</summary>
    public const int DefaultMaxDocumentBytes = 4 * 1024 * 1024;

    /// <summary>Default remote-refresh cadence when <see cref="RefreshTtl" /> is not overridden.</summary>
    public static readonly TimeSpan DefaultRefreshTtl = TimeSpan.FromHours(hours: 24);

    /// <summary>Default per-fetch timeout when <see cref="FetchTimeout" /> is not overridden.</summary>
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(seconds: 20);

    /// <summary>Default retry cadence after a failed refresh, when <see cref="FailureRetryInterval" /> is not overridden.</summary>
    public static readonly TimeSpan DefaultFailureRetryInterval = TimeSpan.FromMinutes(minutes: 1);

    /// <summary>
    ///     Remote catalog URL to refresh from. Ships <strong>empty</strong> — the curated catalog repository
    ///     (<c>https://raw.githubusercontent.com/w0rldx/xe-external/main/catalog/applications.json</c>) does not exist
    ///     yet, so the engine is bundled-only and makes no network call until an operator sets this. A configured
    ///     value is accepted only when it is an absolute <c>https</c> URL, or an <c>http</c> URL whose host is
    ///     <c>127.0.0.1</c>, <c>::1</c> or <c>localhost</c>; anything else is logged once and ignored.
    /// </summary>
    public string? RefreshUrl { get; set; }

    /// <summary>How often a stale in-memory catalog is re-fetched from <see cref="RefreshUrl" />.</summary>
    public TimeSpan RefreshTtl { get; set; } = DefaultRefreshTtl;

    /// <summary>Per-fetch HTTP timeout; a fetch that exceeds this falls back to the last-good/bundled chain.</summary>
    public TimeSpan FetchTimeout { get; set; } = DefaultFetchTimeout;

    /// <summary>
    ///     How long a failed refresh is debounced for while the node is still serving the bundled seed. The full
    ///     <see cref="RefreshTtl" /> would suppress recovery for a day over one failed fetch; no debounce at all would
    ///     make every catalog read hit a dead origin.
    /// </summary>
    public TimeSpan FailureRetryInterval { get; set; } = DefaultFailureRetryInterval;

    /// <summary>Largest catalog document the provider will read; a longer response is a fetch failure.</summary>
    public int MaxDocumentBytes { get; set; } = DefaultMaxDocumentBytes;
}
