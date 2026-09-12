namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Content tests over the REAL shipped catalog, so what we ship is under test rather than a fixture that
///     resembles it. The seed ships EMPTY — Odysseus was a third-party application curated as the first entry and
///     is not ours to ship, and no curated application exists until the XE-owned catalog repository does. What is
///     left here is what stays meaningful for whatever the seed carries: it is a byte copy of the converter's
///     output, and the parity walk that finds the generated document is scoped to this checkout.
/// </summary>
/// <remarks>
///     The per-manifest rules the curated entry used to pin here — digest pinning, no floating <c>latest</c> tag,
///     file bodies hashing to their declared sha256, <c>internet: true</c>, the committed <c>manifestSha256</c>
///     agreeing with <see cref="ExternalAppManifestFingerprint" /> — are validator rules, and
///     <c>ExternalAppCatalogBundledLoaderTests.Load_BundledSeed_PassesTheSameValidatorGateARemoteFetchDoes</c> runs
///     the shipped seed through that gate. The absence of any <c>user</c> key is not a validator rule, because the
///     contract declares no such member. Asserting any of them over an empty application set would verify nothing,
///     so they all live in <see cref="SampleCatalogManifestTests" /> now, over a document that declares a manifest.
/// </remarks>
public sealed class ShippedCatalogSeedTests
{
    private static readonly ExternalAppCatalogDocument Seed = ExternalAppCatalogBundledLoader.Load(NullLogger.Instance);

    [Test]
    public void Seed_ShipsNoCuratedApplication()
    {
        AssertEx.Equal(ExternalAppCatalogValidator.SupportedSchemaVersion, Seed.SchemaVersion);
        AssertEx.Empty(Seed.Applications,
            "the shipped catalog is empty until the XE-owned catalog repository exists; an application reaching it needs this test, the wiki and the roadmap updated together.");
    }

    [Test]
    public async Task Seed_IsByteIdenticalToTheGeneratedCatalog()
    {
        // R1-25: the embedded seed is a copy of the converter's output, never an independently edited file. Only a
        // source checkout carries the generated catalog, so a packaged run skips visibly rather than passing.
        if (ExternalAppCatalogSeed.DistPath is null)
        {
            Skip.Test("catalog/external-apps/dist/applications.json is not reachable from the test binary (not a source checkout).");
        }

        var generated = await File.ReadAllBytesAsync(ExternalAppCatalogSeed.DistPath, CancellationToken.None);

        AssertEx.True(generated.AsSpan().SequenceEqual(ExternalAppCatalogSeed.RawBytes),
            "the embedded seed and catalog/external-apps/dist/applications.json differ; re-run catalog/external-apps/tools/build_catalog.py and commit both.");
    }

    [Test]
    public void FindDistPath_OutsideAnyCheckout_ReturnsNullInsteadOfWalkingOn()
    {
        var root = Directory.CreateTempSubdirectory("xe-external-apps-dist-scope");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "bin", "Release"));

            AssertEx.Null(ExternalAppCatalogSeed.FindDistPath(nested.FullName), "no checkout encloses a temp directory, so there is nothing to compare against.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FindDistPath_WhenTheEnclosingCheckoutHasNoDistFile_FailsInsteadOfEscapingToAnother()
    {
        // Worktrees live under .tmp/worktrees/ inside the main checkout, so an unbounded parent walk finds the MAIN
        // checkout's catalog and Seed_IsByteIdenticalToTheGeneratedCatalog then passes against a file this branch
        // never touched. The walk must stop at the nearest '.git' — a file in a worktree, a directory in a clone.
        var root = Directory.CreateTempSubdirectory("xe-external-apps-dist-scope");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".git"), "gitdir: /nowhere", CancellationToken.None);
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "bin", "Release"));

            var failure = AssertEx.Throws<InvalidOperationException>(() => _ = ExternalAppCatalogSeed.FindDistPath(nested.FullName));

            AssertEx.Contains(failure.Message, "catalog/external-apps/dist/applications.json");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
