namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>
///     Provenance metadata for the curated model catalog in effect (<c>GET model-fit/catalog</c>,
///     <c>POST model-fit/catalog/refresh</c>): which catalog build is active and where it came from.
/// </summary>
/// <remarks>
///     The ranked catalog content rides the recommendations response, in each row's <c>section</c>/<c>tier</c> fields.
///     The one piece of content carried here is <see cref="TestedModels" />, the plain list the Model Management
///     browse panel offers before any search.
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

    /// <summary>
    ///     Every catalog entry flagged <c>tested</c> (the authors ran it through their live scenario checks), ordered by
    ///     <c>totalParamsB</c> ascending, then id.
    /// </summary>
    /// <remarks>
    ///     No llama.cpp arch gate; each row's fit verdict covers its tested quant only. The download dialog shows every
    ///     quant's verdict.
    /// </remarks>
    public required IReadOnlyList<ModelCatalogTestedModelResponse> TestedModels { get; init; }
}

/// <summary>One tested curated-catalog entry, as offered on the Model Management browse panel.</summary>
public sealed class ModelCatalogTestedModelResponse
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Publisher { get; init; }

    /// <summary>The Hugging Face GGUF repository id (<c>owner/repo</c>) the download dialog inspects.</summary>
    public required string GgufRepo { get; init; }

    public required string License { get; init; }

    public required double TotalParamsB { get; init; }

    public string? Notes { get; init; }

    /// <summary>The quant the authors tested (for example <c>Q4_K_M</c> or <c>UD-Q4_K_M</c>), as the download dialog lists it.</summary>
    public required string TestedQuant { get; init; }

    /// <summary>The tested quant's file size in bytes.</summary>
    public required long TestedSizeBytes { get; init; }

    /// <summary>
    ///     The tested quant's fit on this node (<c>Fits</c> / <c>Tight</c> / <c>WontFit</c> / <c>Unknown</c>): the download
    ///     dialog's rule, graded against the memoized hardware profile rather than the live process budget; <c>Unknown</c> until
    ///     the node has a device audit.
    /// </summary>
    public required string FitVerdict { get; init; }
}
