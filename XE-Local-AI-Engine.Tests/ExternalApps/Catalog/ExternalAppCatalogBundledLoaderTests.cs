namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Loads the ACTUAL embedded <c>external-apps-catalog.seed.json</c> resource (not a synthetic fixture), so a
///     content error in the shipped catalog fails here rather than at the first install, and proves the
///     degrade-to-empty path a missing resource takes: the node must come up bundled-empty with a loud Error, never
///     throw out of the DI graph.
/// </summary>
public sealed class ExternalAppCatalogBundledLoaderTests
{
    [Test]
    public void Load_BundledSeed_IsValidAndContainsOdysseus()
    {
        var document = ExternalAppCatalogBundledLoader.Load(NullLogger.Instance);

        AssertEx.Equal(ExternalAppCatalogValidator.SupportedSchemaVersion, document.SchemaVersion);
        AssertEx.ContainsSingle(document.Applications, manifest => string.Equals(manifest.Id, "odysseus", StringComparison.Ordinal));
    }

    [Test]
    public void Load_BundledSeed_PassesTheSameValidatorGateARemoteFetchDoes()
    {
        // The loader validates before serving; this asserts the seed's own text against the gate directly, so a
        // failure names the rule that broke rather than an empty catalog three assertions later.
        var validation = ExternalAppCatalogValidator.Validate(ExternalAppCatalogSeed.RawJson);

        AssertEx.True(validation.IsValid,
            $"the embedded seed must pass the catalog validator; first error: {(validation.Errors.Count > 0 ? validation.Errors[0] : "(none)")}");
    }

    [Test]
    public void Load_WhenResourceIsMissing_DegradesToAnEmptyCatalogWithoutThrowing()
    {
        var logger = new RecordingLogger<ExternalAppCatalogBundledLoaderTests>();

        // Any assembly that embeds no catalog seed exercises the missing-resource branch.
        var document = ExternalAppCatalogBundledLoader.Load(logger, typeof(ExternalAppCatalogBundledLoaderTests).Assembly);

        AssertEx.Equal(ExternalAppCatalogValidator.SupportedSchemaVersion, document.SchemaVersion);
        AssertEx.Empty(document.Applications);
        AssertEx.True(logger.HasEntry(LogLevel.Error, "was not found"), "the missing seed must be reported at Error, not swallowed.");
    }
}
