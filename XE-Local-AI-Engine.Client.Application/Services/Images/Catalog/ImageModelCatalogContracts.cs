namespace XE_Local_AI_Engine.Client.Services.Images.Catalog;

/// <summary>
///     The curated image-model catalog document (schema v1). Bundled as an embedded resource
///     (<c>image-model-catalog.seed.json</c>) and validated through <see cref="ImageModelCatalogValidator" /> before it
///     is ever served. Editorial by design — like the GGUF <c>model-catalog.seed.json</c> it mirrors, the entries are
///     hand-picked and each one's repo/file/size was verified against the live Hub API before being added, because a
///     one-click install that 404s is worse than an absent row.
/// </summary>
public sealed record ImageModelCatalogDocument(
    int SchemaVersion,
    string CatalogVersion,
    string? UpdatedAt,
    IReadOnlyList<ImageModelCatalogEntry> Models);

/// <summary>
///     One curated image model: everything <c>POST images/models/downloads</c> needs, so installing it is a single
///     click instead of a repo id, a file name, a family and a size typed by hand.
///     <para>
///         <see cref="Parts" /> is the whole file-set. A part may name its own <c>RepoId</c> — the Qwen-Image set is
///         genuinely split across repositories — and every part declares its size, which is what makes the free-disk
///         pre-flight run and the aggregate download percentage computable.
///     </para>
/// </summary>
public sealed record ImageModelCatalogEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string RepoId,
    string Family,
    string License,
    bool Recommended,
    string? Notes,
    IReadOnlyList<ImageModelCatalogPart> Parts);

/// <summary>One weight file of a curated entry's file-set.</summary>
public sealed record ImageModelCatalogPart(string Role, string FileName, string? RepoId, long SizeBytes);

/// <summary>
///     Result of <see cref="ImageModelCatalogValidator.Validate" />. Mirrors the GGUF catalog's validation result:
///     a document with any invalid entry is rejected wholesale rather than partially loaded, so a bad edit can never
///     surface half a catalog of broken install buttons.
/// </summary>
public sealed record ImageModelCatalogValidationResult
{
    private ImageModelCatalogValidationResult(bool isValid, ImageModelCatalogDocument? document, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Document = document;
        Errors = errors;
    }

    public bool IsValid { get; }

    public ImageModelCatalogDocument? Document { get; }

    public IReadOnlyList<string> Errors { get; }

    public static ImageModelCatalogValidationResult Success(ImageModelCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ImageModelCatalogValidationResult(isValid: true, document, []);
    }

    public static ImageModelCatalogValidationResult Failure(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new ImageModelCatalogValidationResult(isValid: false, document: null, errors);
    }
}
