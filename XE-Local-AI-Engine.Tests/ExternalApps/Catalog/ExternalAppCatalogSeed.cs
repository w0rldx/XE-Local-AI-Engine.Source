namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Text;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Reads the REAL embedded <c>external-apps-catalog.seed.json</c> resource out of the application assembly, plus
///     the in-repo authoring output it is copied from. Used by the tests that assert what the node actually ships.
///     A test needing a document that declares an application uses
///     <c>XE_Local_AI_Engine.Client.Testing.ExternalApps.SampleCatalogManifest</c> instead: the shipped seed declares
///     none.
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
    /// <param name="startDirectory">The directory the walk starts at, inclusive.</param>
    /// <param name="highestDirectory">The last directory the walk may inspect; <see langword="null" /> walks on to the filesystem root.</param>
    /// <remarks>
    ///     The production caller passes no boundary. A caller that owns only part of the chain passes the top of what
    ///     it owns: the OS temp root is shared, so a <c>.git</c> another process leaves above a temp directory would
    ///     otherwise pull an unbounded walk into a checkout the caller never created.
    /// </remarks>
    internal static string? FindDistPath(string startDirectory, string? highestDirectory = null)
    {
        var boundary = highestDirectory is null ? null : Path.TrimEndingDirectorySeparator(highestDirectory);
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            // `.git` is a directory in a clone and a file in a linked worktree; either spelling marks the root.
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                var candidate = Path.Combine(directory.FullName, "catalog", "external-apps", "dist", "applications.json");
                return File.Exists(candidate)
                    ? candidate
                    : throw new InvalidOperationException(
                        $"The checkout at '{directory.FullName}' has no catalog/external-apps/dist/applications.json; regenerate it with catalog/external-apps/tools/build_catalog.py.");
            }

            if (boundary is not null && string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), boundary, StringComparison.Ordinal))
            {
                break;
            }
        }

        return null;
    }
}
