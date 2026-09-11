namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Content tests over the REAL shipped catalog, so the curated Odysseus entry itself is under test rather than a
///     fixture that resembles it. The validator proves a document is well-formed; these prove the document we ship is
///     the one that was reviewed — the published ports, the single open target, the storage layout, the required
///     variables, the capability set, the file hashes, the absence of any <c>user</c> field, and the cross-language
///     fingerprint agreement between the Python converter and <see cref="ExternalAppManifestFingerprint" />.
/// </summary>
public sealed partial class OdysseusSeedManifestTests
{
    private const string ApplicationId = "odysseus";

    private static readonly ExternalAppCatalogDocument Seed = ExternalAppCatalogBundledLoader.Load(NullLogger.Instance);

    private static ApplicationManifest Odysseus => Seed.Applications.Single(manifest => string.Equals(manifest.Id, ApplicationId, StringComparison.Ordinal));

    [Test]
    public void Seed_Odysseus_DeclaresTheFourReviewedServices()
    {
        AssertEx.Equal(expected: 1, Seed.Applications.Count);
        AssertEx.Equal("odysseus,chromadb,searxng,ntfy", string.Join(',', Odysseus.Services.Select(service => service.Name)));
    }

    [Test]
    public void Seed_Odysseus_PublishesOnlyTheOdysseusAndNtfyPorts()
    {
        var published = Odysseus.Services
                                .SelectMany(service => service.Ports.Select(port => string.Create(CultureInfo.InvariantCulture, $"{service.Name}:{port.ContainerPort}")))
                                .ToArray();

        AssertEx.Equal("odysseus:7000,ntfy:80", string.Join(',', published));
    }

    [Test]
    public void Seed_Odysseus_DeclaresExactlyOneOpenTarget()
    {
        // Rule A17 and the S4 "Open" action both depend on there being exactly one: the UI opens the port carrying a
        // path, not the first published one.
        var openTargets = Odysseus.Services
                                  .SelectMany(service => service.Ports)
                                  .Where(port => port.OpenPath is not null)
                                  .ToArray();

        AssertEx.Equal(expected: 1, openTargets.Length);
        AssertEx.Equal("/", openTargets[0].OpenPath!);
        AssertEx.Equal(expected: 7000, openTargets[0].ContainerPort);
    }

    [Test]
    public void Seed_Odysseus_PinsEveryImageByDigestAndNeverToLatest()
    {
        foreach (var service in Odysseus.Services)
        {
            AssertEx.Contains(service.Image, "@sha256:");
            AssertEx.False(service.ImageTag.Contains("latest", StringComparison.OrdinalIgnoreCase),
                $"{service.Name}: a floating 'latest' tag makes the pinned digest unreproducible.");
        }
    }

    [Test]
    public void Seed_Odysseus_DeclaresTheFiveDataPathsAndFourSidecarVolumes()
    {
        var storageCounts = Odysseus.Services.ToDictionary(service => service.Name, service => service.Storage.Count, StringComparer.Ordinal);

        AssertEx.Equal(expected: 5, storageCounts["odysseus"]);
        AssertEx.Equal(expected: 1, storageCounts["chromadb"]);
        // searxng declares TWO: the image carries a VOLUME for each, and an image VOLUME the manifest does not
        // declare is an undeclared mount on the created container, which the policy refuses after start.
        AssertEx.Equal(expected: 2, storageCounts["searxng"]);
        AssertEx.Equal(expected: 1, storageCounts["ntfy"]);
    }

    /// <summary>
    ///     Every volume the four images declare has to appear in the manifest's storage. The S5 live round installed
    ///     Odysseus for real and the install failed its post-start policy check on searxng's /var/cache/searxng, which
    ///     upstream's compose leaves anonymous; this pins the corrected set so it cannot silently regress.
    /// </summary>
    [Test]
    public void Seed_Odysseus_DeclaresEveryVolumeItsImagesCarry()
    {
        var storagePaths = Odysseus.Services.ToDictionary(
            service => service.Name,
            service => string.Join(',', service.Storage.Select(entry => entry.ContainerPath).Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);

        AssertEx.Equal("/app/.cache/huggingface,/app/.local,/app/.ssh,/app/data,/app/logs", storagePaths["odysseus"]);
        AssertEx.Equal("/chroma/chroma", storagePaths["chromadb"]);
        AssertEx.Equal("/etc/searxng,/var/cache/searxng", storagePaths["searxng"]);
        AssertEx.Equal("/var/cache/ntfy", storagePaths["ntfy"]);
    }

    [Test]
    public void Seed_Odysseus_RequiresAnAdminUserAndSecretPassword()
    {
        var required = Odysseus.Variables.Where(variable => variable.Required).ToArray();

        AssertEx.Equal("ODYSSEUS_ADMIN_USER,ODYSSEUS_ADMIN_PASSWORD", string.Join(',', required.Select(variable => variable.Name)));
        AssertEx.ContainsSingle(required, variable => string.Equals(variable.Type, "secret", StringComparison.Ordinal));
        // The admin user name ships a benign default; the password must not, and neither must any other secret (D11).
        AssertEx.True(Odysseus.Variables.Where(variable => string.Equals(variable.Type, "secret", StringComparison.Ordinal)).All(variable => variable.Default is null),
            "a secret variable must never ship a default — least of all the admin password.");
    }

    [Test]
    public void Seed_Odysseus_CapAddIsWithinTheAllowList()
    {
        foreach (var service in Odysseus.Services)
        {
            foreach (var capability in service.CapAdd)
            {
                AssertEx.Contains(ExternalAppCatalogValidator.AllowedCapAdd, capability,
                    $"{service.Name}: capability '{capability}' is outside Docker's default set.");
            }
        }
    }

    [Test]
    public void Seed_EveryFileEntry_MatchesItsDeclaredSha256()
    {
        var files = Seed.Applications.SelectMany(manifest => manifest.Services).SelectMany(service => service.Files).ToArray();

        AssertEx.NotEmpty(files);
        foreach (var file in files)
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(file.ContentBase64)));
            AssertEx.Equal(file.Sha256, digest, $"file '{file.Source}' does not hash to its declared sha256.");
        }
    }

    [Test]
    public void Seed_OdysseusEnvironmentTokens_AllResolveToDeclaredVariablesOrBuiltIns()
    {
        var declared = Odysseus.Variables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        var servicesWithUiPort = Odysseus.Services.Where(service => service.Ports.Count > 0).Select(service => service.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in Odysseus.Services)
        {
            foreach (var (key, value) in service.Environment)
            {
                foreach (var match in TokenPattern().Matches(value).Cast<Match>())
                {
                    var token = match.Groups[1].Value;
                    var resolves = declared.Contains(token)
                                   || ExternalAppCatalogValidator.BuiltInVariableNames.Contains(token)
                                   || (token.StartsWith(ExternalAppCatalogValidator.BuiltInUiPortPrefix, StringComparison.Ordinal)
                                       && servicesWithUiPort.Contains(token[ExternalAppCatalogValidator.BuiltInUiPortPrefix.Length..]));

                    AssertEx.True(resolves, $"{service.Name}.{key}: '${{{token}}}' resolves to no declared variable, built-in, or published UI port.");
                }
            }
        }
    }

    [Test]
    public void Seed_NoManifestDeclaresAUserField()
    {
        // R1-14/R1-26: application containers start as the image's default user and the engine never passes --user.
        // The contract carries no such member, so this checks the shipped JSON rather than the deserialized graph:
        // a stray key would round-trip away unnoticed.
        var offenders = new List<string>();
        CollectPropertyPaths(JsonNode.Parse(ExternalAppCatalogSeed.RawJson)!, path: "$", "user", offenders);

        AssertEx.Empty(offenders, $"the seed declares a 'user' key at: {string.Join(", ", offenders)}");
    }

    [Test]
    public void Seed_EveryManifest_DeclaresInternetAccess()
    {
        foreach (var manifest in Seed.Applications)
        {
            AssertEx.True(manifest.Permissions.Internet, $"{manifest.Id}: rule A20 makes internet:false unreachable — V1 enforces no outbound restriction.");
        }
    }

    [Test]
    public void Seed_EveryManifest_MatchesTheFingerprintTheConverterWrote()
    {
        // The one test proving the C# and Python canonicalisations agree byte for byte: the hash on the right was
        // written by build_catalog.py, the one on the left is recomputed here. A property-ordering, null-handling or
        // escaping divergence surfaces here rather than as a rejected install in S2.
        foreach (var manifest in Seed.Applications)
        {
            AssertEx.Equal(manifest.ManifestSha256, ExternalAppManifestFingerprint.Compute(manifest), $"{manifest.Id}: recomputed fingerprint differs from the committed manifestSha256.");
        }
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

    private static void CollectPropertyPaths(JsonNode node, string path, string propertyName, List<string> found)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (key, value) in jsonObject)
                {
                    if (string.Equals(key, propertyName, StringComparison.Ordinal))
                    {
                        found.Add($"{path}.{key}");
                    }

                    if (value is not null)
                    {
                        CollectPropertyPaths(value, $"{path}.{key}", propertyName, found);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is { } item)
                    {
                        CollectPropertyPaths(item, $"{path}[{index}]", propertyName, found);
                    }
                }

                break;

            default:
                break;
        }
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_-]*)\}")]
    private static partial Regex TokenPattern();
}
