namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     The curated model catalog document (schema v1), bundled as the embedded <c>model-catalog.seed.json</c>
///     resource and optionally replaced by a remote refresh (<see cref="ModelCatalogOptions.RefreshUrl" />).
/// </summary>
/// <remarks>
///     Editorial: tiers, use-case tags and notes are hand-assigned — there is no external quality-score API.
/// </remarks>
public sealed record ModelCatalogDocument(
    int SchemaVersion,
    string CatalogVersion,
    string? UpdatedAt,
    IReadOnlyList<ModelCatalogEntry> Models);

/// <summary>
///     One curated catalog entry: a specific model family pointed at a live-verified Hugging Face GGUF repo.
/// </summary>
/// <remarks>
///     <see cref="ActiveParamsB" /> non-null (with <see cref="Moe" /> true) marks a Mixture-of-Experts model and feeds
///     <c>MoeFacts.ActiveParamCount</c> for the expert-offload fit estimate. <see cref="MinLlamaCppTag" /> is a
///     <c>bNNNN</c> release tag compared numerically against the node's installed-else-pinned llama.cpp build
///     (<see cref="ModelCatalogArchGate" />): an entry whose architecture the pinned runtime cannot yet run is
///     excluded, never shown as a broken recommendation.
/// </remarks>
public sealed record ModelCatalogEntry(
    string Id,
    string Family,
    string DisplayName,
    string Publisher,
    string GgufRepo,
    string License,
    string Tier,
    IReadOnlyList<string> UseCases,
    double TotalParamsB,
    double? ActiveParamsB,
    bool Moe,
    int ContextLength,
    string MinLlamaCppTag,
    string ReleaseDate,
    string? Notes);

/// <summary>
///     Result of <see cref="ModelCatalogValidator.Validate" />: a well-formed document
///     (<see cref="Document" /> non-null, <see cref="Errors" /> empty) or a validation failure, each problem described
///     with its <c>models[i].field</c> path.
/// </summary>
/// <remarks>
///     The caller — bundled loader or remote refresh — never propagates a malformed catalog into the recommendation
///     pipeline.
/// </remarks>
public sealed record ModelCatalogValidationResult
{
    private ModelCatalogValidationResult(bool isValid, ModelCatalogDocument? document, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Document = document;
        Errors = errors;
    }

    public bool IsValid { get; }

    public ModelCatalogDocument? Document { get; }

    public IReadOnlyList<string> Errors { get; }

    public static ModelCatalogValidationResult Success(ModelCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ModelCatalogValidationResult(isValid: true, document, []);
    }

    public static ModelCatalogValidationResult Failure(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new ModelCatalogValidationResult(isValid: false, document: null, errors);
    }
}

/// <summary>Where a served <see cref="ModelCatalogDocument" /> came from — surfaced on the catalog-info endpoint.</summary>
public enum ModelCatalogSource
{
    /// <summary>The embedded, in-repo seed catalog (no remote refresh configured, or none has ever succeeded).</summary>
    Bundled = 0,

    /// <summary>Freshly fetched and validated from <see cref="ModelCatalogOptions.RefreshUrl" /> this run.</summary>
    Remote = 1,

    /// <summary>
    ///     A previously-fetched remote catalog persisted to disk, served because the latest remote attempt failed on
    ///     the network or on validation.
    /// </summary>
    /// <remarks>
    ///     The fallback chain never regresses recommendations to bundled-only once a remote catalog has been fetched
    ///     successfully.
    /// </remarks>
    RemoteLastGood = 2
}

/// <summary>
///     The catalog currently in effect plus its provenance. <see cref="FetchedAtUtc" /> is <see langword="null" /> only
///     for <see cref="ModelCatalogSource.Bundled" />.
/// </summary>
public sealed class ModelCatalogSnapshot
{
    public required ModelCatalogDocument Document { get; init; }

    public required ModelCatalogSource Source { get; init; }

    public required DateTimeOffset? FetchedAtUtc { get; init; }

    public required string? SourceUrl { get; init; }
}
