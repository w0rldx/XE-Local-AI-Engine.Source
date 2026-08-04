namespace XE_Local_AI_Engine.Client.Services.Images.Catalog.Implementation;

/// <summary>
///     <see cref="IImageModelCatalog" /> over the embedded seed. Loads and validates once at construction (the document
///     is immutable and the assembly resource cannot change at runtime), so serving the catalog costs nothing per
///     request.
/// </summary>
internal sealed class ImageModelCatalog : IImageModelCatalog
{
    private readonly ImageModelCatalogDocument _document;

    public ImageModelCatalog(ILogger<ImageModelCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _document = ImageModelCatalogBundledLoader.Load(logger);
    }

    /// <inheritdoc />
    public ImageModelCatalogDocument GetDocument()
    {
        return _document;
    }
}
