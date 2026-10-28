namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Testing.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ApplicationManifest" />, <see cref="ExternalAppCatalogSnapshot" /> and
///     <see cref="StoredExternalAppCatalogCache" /> suppress the record printer, so one
///     <c>LogDebug("{Snapshot}", snapshot)</c> cannot write the catalog (file bodies, variable defaults, the fetched
///     document) into the node log. Mirrors <c>ContainerRuntimeRecordPrintingTests</c>.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalAppCatalogRecordPrintingTests
{
    [Test]
    public void ToString_OnTheCatalogRecords_PrintsNoCatalogContent()
    {
        // The shipped seed declares no application, so the fixture is the only document that can prove a manifest
        // does not leak: with nothing to print, every assertion below would pass vacuously.
        var document = AssertEx.NotNull(ExternalAppCatalogValidator.Validate(SampleCatalogManifest.RawJson).Document);
        var manifest = document.Applications[0];
        var snapshot = new ExternalAppCatalogSnapshot(document,
            ExternalAppCatalogSource.Bundled,
            FetchedAtUtc: null,
            SourceUrl: null,
            LastRefreshFailure: null);
        var cache = new StoredExternalAppCatalogCache(SampleCatalogManifest.RawJson,
            DateTimeOffset.UnixEpoch,
            "https://catalog.test/applications.json",
            ETag: null);

        AssertEx.NotEmpty(manifest.Id);
        AssertEx.NotEmpty(manifest.ManifestSha256);

        foreach (var (record, printed) in new[]
                 {
                     ("ApplicationManifest", manifest.ToString()),
                     ("ExternalAppCatalogSnapshot", snapshot.ToString()),
                     ("StoredExternalAppCatalogCache", cache.ToString())
                 })
        {
            AssertEx.False(printed.Contains(manifest.Id, StringComparison.Ordinal),
                $"{record}.ToString() printed the application id: {printed}");
            AssertEx.False(printed.Contains(manifest.DisplayName, StringComparison.Ordinal),
                $"{record}.ToString() printed the display name: {printed}");
            AssertEx.False(printed.Contains(manifest.ManifestSha256, StringComparison.Ordinal),
                $"{record}.ToString() printed the manifest fingerprint: {printed}");
            AssertEx.False(printed.Contains("schemaVersion", StringComparison.OrdinalIgnoreCase),
                $"{record}.ToString() printed the raw catalog document: {printed}");
        }
    }
}
