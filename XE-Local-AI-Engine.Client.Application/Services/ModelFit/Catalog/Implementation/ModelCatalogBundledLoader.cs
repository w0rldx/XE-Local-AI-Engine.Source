namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;

/// <summary>
///     Loads the embedded <c>model-catalog.seed.json</c> resource — the always-available fallback the provider serves
///     when no remote refresh is configured, a remote fetch fails, and no last-good persisted copy exists. Validated
///     through the same <see cref="ModelCatalogValidator" /> gate as a remote fetch: a bundled-content bug must never
///     crash the app, so a failed validation degrades to an empty (but schema-valid) catalog with a loud error log
///     rather than throwing out of the DI graph.
/// </summary>
internal static class ModelCatalogBundledLoader
{
    private const string ResourceNameSuffix = "model-catalog.seed.json";

    public static ModelCatalogDocument Load(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var assembly = typeof(ModelCatalogBundledLoader).Assembly;

        // Match by suffix so the loader is robust to the assembly's manifest-resource-name prefix (root namespace +
        // folder path), mirroring AgentTemplateCatalog's embedded-resource lookup.
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            logger.LogError("Embedded bundled model catalog resource '{ResourceNameSuffix}' was not found; serving an empty catalog.", ResourceNameSuffix);
            return EmptyDocument();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            logger.LogError("Embedded bundled model catalog resource '{ResourceName}' could not be opened; serving an empty catalog.", resourceName);
            return EmptyDocument();
        }

        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();

        var validation = ModelCatalogValidator.Validate(raw);
        if (!validation.IsValid)
        {
            logger.LogError(
                "Bundled model catalog failed validation ({ErrorCount} error(s)); serving an empty catalog. First error: {FirstError}",
                validation.Errors.Count,
                validation.Errors.Count > 0 ? validation.Errors[0] : "(none)");
            return EmptyDocument();
        }

        return validation.Document!;
    }

    private static ModelCatalogDocument EmptyDocument()
    {
        return new ModelCatalogDocument(ModelCatalogValidator.SupportedSchemaVersion, "0.0.0-empty", UpdatedAt: null, Models: []);
    }
}
