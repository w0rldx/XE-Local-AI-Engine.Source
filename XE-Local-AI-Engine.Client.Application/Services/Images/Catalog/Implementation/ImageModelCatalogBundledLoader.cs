namespace XE_Local_AI_Engine.Client.Services.Images.Catalog.Implementation;

/// <summary>
///     Loads the embedded <c>image-model-catalog.seed.json</c> resource. Validated through the same
///     <see cref="ImageModelCatalogValidator" /> gate a hand edit would be: a bundled-content bug must never crash the
///     app, so a failed validation degrades to an empty (but schema-valid) catalog with a loud error log rather than
///     throwing out of the DI graph. Mirrors <c>ModelCatalogBundledLoader</c>.
/// </summary>
internal static class ImageModelCatalogBundledLoader
{
    private const string ResourceNameSuffix = "image-model-catalog.seed.json";

    public static ImageModelCatalogDocument Load(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var assembly = typeof(ImageModelCatalogBundledLoader).Assembly;

        // Match by suffix so the loader is robust to the assembly's manifest-resource-name prefix (root namespace +
        // folder path), mirroring the GGUF catalog's embedded-resource lookup.
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            logger.LogError("Embedded image model catalog resource '{ResourceNameSuffix}' was not found; serving an empty catalog.", ResourceNameSuffix);
            return EmptyDocument();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            logger.LogError("Embedded image model catalog resource '{ResourceName}' could not be opened; serving an empty catalog.", resourceName);
            return EmptyDocument();
        }

        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();

        var validation = ImageModelCatalogValidator.Validate(raw);
        if (!validation.IsValid)
        {
            logger.LogError("Bundled image model catalog failed validation ({ErrorCount} error(s)); serving an empty catalog. First error: {FirstError}",
                validation.Errors.Count,
                validation.Errors.Count > 0 ? validation.Errors[0] : "(none)");
            return EmptyDocument();
        }

        return validation.Document!;
    }

    private static ImageModelCatalogDocument EmptyDocument()
    {
        return new ImageModelCatalogDocument(ImageModelCatalogValidator.SupportedSchemaVersion, "0.0.0-empty", UpdatedAt: null, Models: []);
    }
}
