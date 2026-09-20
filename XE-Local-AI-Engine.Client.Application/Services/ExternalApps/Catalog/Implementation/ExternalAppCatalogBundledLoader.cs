namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

using System.Reflection;

/// <summary>Loads the embedded <c>external-apps-catalog.seed.json</c> resource: the always-available catalog served when no remote or last-good copy is usable.</summary>
/// <remarks>
///     Validated through the same <see cref="ExternalAppCatalogValidator" /> gate as a remote fetch. A bundled-content
///     bug must never crash the node, so a failed validation degrades to an empty (but schema-valid) catalog with a
///     loud error log rather than throwing out of the DI graph. Mirrors <c>ModelCatalogBundledLoader</c>.
/// </remarks>
internal static class ExternalAppCatalogBundledLoader
{
    private const string ResourceNameSuffix = "external-apps-catalog.seed.json";

    /// <summary>The <c>generatedAtUtc</c> the degraded empty document carries; it is never served as a real catalog.</summary>
    private const string EmptyGeneratedAtUtc = "1970-01-01T00:00:00Z";

    public static ExternalAppCatalogDocument Load(ILogger logger)
    {
        return Load(logger, typeof(ExternalAppCatalogBundledLoader).Assembly);
    }

    /// <summary>Overload naming the assembly to read the seed from; production always passes this one's own assembly.</summary>
    /// <remarks>
    ///     The seam exists so the degrade-to-empty path — unreachable while the resource is embedded — is provable by
    ///     a test that passes an assembly carrying no such resource.
    /// </remarks>
    public static ExternalAppCatalogDocument Load(ILogger logger, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(assembly);

        // Match by suffix so the loader is robust to the assembly's manifest-resource-name prefix (root namespace +
        // folder path), mirroring ModelCatalogBundledLoader's embedded-resource lookup.
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            logger.LogError("Embedded bundled External Apps catalog resource '{ResourceNameSuffix}' was not found; serving an empty catalog.", ResourceNameSuffix);
            return EmptyDocument();
        }

        // Forced sync: the only production caller is the ApplicationCatalogProvider constructor and the tests read it
        // from a static field initializer (ShippedCatalogSeedTests.Seed) — neither can await, and the read is in-memory.
#pragma warning disable MA0045 // forced sync: constructor-time embedded-resource read (see comment above)
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            logger.LogError("Embedded bundled External Apps catalog resource '{ResourceName}' could not be opened; serving an empty catalog.", resourceName);
            return EmptyDocument();
        }

        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();
#pragma warning restore MA0045

        var validation = ExternalAppCatalogValidator.Validate(raw);
        if (!validation.IsValid)
        {
            logger.LogError("Bundled External Apps catalog failed validation ({ErrorCount} error(s)); serving an empty catalog. First error: {FirstError}",
                validation.Errors.Count,
                validation.Errors.Count > 0 ? validation.Errors[0] : "(none)");
            return EmptyDocument();
        }

        return validation.Document!;
    }

    private static ExternalAppCatalogDocument EmptyDocument()
    {
        return new ExternalAppCatalogDocument(ExternalAppCatalogValidator.SupportedSchemaVersion, EmptyGeneratedAtUtc, Applications: []);
    }
}
