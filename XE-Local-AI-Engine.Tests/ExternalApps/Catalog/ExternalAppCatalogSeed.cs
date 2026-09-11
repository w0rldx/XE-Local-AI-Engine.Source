namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Text;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Reads the REAL embedded <c>external-apps-catalog.seed.json</c> resource out of the application assembly, plus
///     the in-repo authoring output it is copied from. Shared by the provider tests (which serve the seed as a
///     synthetic "remote" body, so no test has to hand-write a document whose <c>manifestSha256</c> would then have to
///     be recomputed) and by the seed-content tests.
/// </summary>
internal static class ExternalAppCatalogSeed
{
    private const string ResourceNameSuffix = "external-apps-catalog.seed.json";

    /// <summary>The seed resource exactly as it is embedded, byte for byte.</summary>
    public static byte[] RawBytes { get; } = ReadEmbeddedSeed();

    /// <summary>The seed document decoded as UTF-8 text.</summary>
    public static string RawJson { get; } = Encoding.UTF8.GetString(RawBytes);

    /// <summary>
    ///     The generated catalog under <c>catalog/external-apps/dist/</c>, or <see langword="null" /> when the test
    ///     binary runs outside a source checkout, in which case the byte-identity check skips visibly rather than
    ///     reporting a pass.
    /// </summary>
    public static string? DistPath { get; } = FindDistPath(AppContext.BaseDirectory);

    /// <summary>
    ///     Returns the seed with a different document-level <c>generatedAtUtc</c> — the one field no manifest
    ///     fingerprint covers, so the result is still a valid document and is distinguishable from the bundled copy.
    /// </summary>
    public static string WithGeneratedAtUtc(string generatedAtUtc)
    {
        var document = JsonNode.Parse(RawJson)!.AsObject();
        document["generatedAtUtc"] = generatedAtUtc;
        return document.ToJsonString();
    }

    private static byte[] ReadEmbeddedSeed()
    {
        var assembly = typeof(ExternalAppCatalogValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    ///     The dist file of the checkout <paramref name="startDirectory" /> sits in, or <see langword="null" /> when
    ///     no checkout encloses it. The walk stops at the nearest checkout root rather than at the first directory
    ///     that happens to hold a dist file: worktrees live under <c>.tmp/worktrees/</c> inside the main checkout, so
    ///     an unbounded walk finds the MAIN checkout's catalog and the parity test then passes against a file this
    ///     branch never touched. A root without the dist file is a broken checkout, not a reason to skip.
    /// </summary>
    internal static string? FindDistPath(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            // `.git` is a directory in a clone and a file in a linked worktree; either spelling marks the root.
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
            {
                continue;
            }

            var candidate = Path.Combine(directory.FullName, "catalog", "external-apps", "dist", "applications.json");
            return File.Exists(candidate)
                ? candidate
                : throw new InvalidOperationException(
                    $"The checkout at '{directory.FullName}' has no catalog/external-apps/dist/applications.json; regenerate it with catalog/external-apps/tools/build_catalog.py.");
        }

        return null;
    }
}
