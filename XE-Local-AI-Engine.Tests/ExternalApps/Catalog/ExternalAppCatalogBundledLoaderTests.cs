namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Loads the ACTUAL embedded <c>external-apps-catalog.seed.json</c> resource (not a synthetic fixture), so a
///     content error in the shipped catalog fails here rather than at the first install, and proves the
///     degrade-to-empty path a missing resource takes: the node must come up bundled-empty with a loud Error, never
///     throw out of the DI graph. The shipped seed itself declares no application today, which is why the
///     missing-resource case asserts the Error log and not merely an empty catalog: the two are otherwise
///     indistinguishable.
/// </summary>
public sealed class ExternalAppCatalogBundledLoaderTests
{
    [Test]
    public void Load_BundledSeed_IsValidAndShipsNoApplication()
    {
        var logger = new RecordingLogger<ExternalAppCatalogBundledLoaderTests>();

        var document = ExternalAppCatalogBundledLoader.Load(logger);

        AssertEx.Equal(ExternalAppCatalogValidator.SupportedSchemaVersion, document.SchemaVersion);
        AssertEx.Empty(document.Applications, "the shipped catalog is empty until the XE-owned catalog repository exists.");
        // An empty catalog is also what a missing or invalid resource degrades to, so the absence of an Error is
        // what separates 'we ship nothing' from 'the seed did not load'.
        AssertEx.Empty(logger.Entries.Where(entry => entry.Level == LogLevel.Error), "the embedded seed must load cleanly, not degrade to empty with an Error.");
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
