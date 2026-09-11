namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     R3-23: the three catalog records that can carry a whole manifest — <see cref="ApplicationManifest" />,
///     <see cref="ExternalAppCatalogSnapshot" /> and <see cref="StoredExternalAppCatalogCache" /> — suppress the
///     record printer, so one <c>LogDebug("{Snapshot}", snapshot)</c> downstream cannot write the catalog (inlined
///     file bodies, variable defaults, the raw fetched document) into the node log. Mirrors
///     <c>ContainerRuntimeRecordPrintingTests</c> on the container side.
/// </summary>
public sealed class ExternalAppCatalogRecordPrintingTests
{
    [Test]
    public void ToString_OnTheCatalogRecords_PrintsNoCatalogContent()
    {
        var document = ExternalAppCatalogBundledLoader.Load(NullLogger.Instance);
        var manifest = document.Applications[0];
        var snapshot = new ExternalAppCatalogSnapshot(document,
            ExternalAppCatalogSource.Bundled,
            FetchedAtUtc: null,
            SourceUrl: null,
            LastRefreshFailure: null);
        var cache = new StoredExternalAppCatalogCache(ExternalAppCatalogSeed.RawJson,
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
