namespace XE_Local_AI_Engine.Client.Testing.ExternalApps;

using System.Text;
using System.Text.Json.Nodes;

/// <summary>
///     A hand-authored External Apps catalog document for tests needing more than the empty shipped
///     seed: <c>dependsOn</c> conditions, a host-gateway extra host, shared storage, an inlined file asset, a
///     capability add-back and every variable type.
/// </summary>
/// <remarks>
///     The document is never produced by <c>catalog/external-apps/tools/build_catalog.py</c> and is never shipped;
///     its image digests are well-formed but fabricated. Its <c>manifestSha256</c> is stamped by hand and pinned by
///     <c>SampleCatalogManifestTests</c> against a fresh <c>ExternalAppManifestFingerprint.Compute</c>, so an edit
///     that forgets to re-stamp the hash fails loudly instead of producing a manifest an install would reject.
/// </remarks>
public static class SampleCatalogManifest
{
    private const string ResourceNameSuffix = "sample-catalog-manifest.json";

    /// <summary>The id of the single application the fixture declares.</summary>
    public const string ApplicationId = "sample-httpd";

    /// <summary>The fixture document exactly as it is embedded.</summary>
    public static string RawJson { get; } = ReadEmbeddedDocument();

    /// <summary>
    ///     The fixture with a different document-level <c>generatedAtUtc</c> — the one field no manifest fingerprint
    ///     covers, so the result is still a valid document and is distinguishable from the embedded copy.
    /// </summary>
    public static string WithGeneratedAtUtc(string generatedAtUtc)
    {
        var document = JsonNode.Parse(RawJson)!.AsObject();
        document["generatedAtUtc"] = generatedAtUtc;
        return document.ToJsonString();
    }

    private static string ReadEmbeddedDocument()
    {
        var assembly = typeof(SampleCatalogManifest).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
