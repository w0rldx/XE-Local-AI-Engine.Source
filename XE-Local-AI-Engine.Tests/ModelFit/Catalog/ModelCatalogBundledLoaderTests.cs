namespace XE_Local_AI_Engine.Tests.ModelFit.Catalog;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Loads the ACTUAL embedded <c>model-catalog.seed.json</c> resource (not a synthetic fixture) — catches a schema
///     typo in the real seed content that unit tests against synthetic JSON would never see. Every entry must also
///     declare at least one of the six allowlisted use-cases and a live-verifiable "owner/repo" GGUF id shape.
/// </summary>
public sealed class ModelCatalogBundledLoaderTests
{
    [Test]
    public void Load_BundledSeedCatalog_IsValidAndNonEmpty()
    {
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);

        AssertEx.True(document.Models.Count >= 30, $"expected a substantial seed catalog, got {document.Models.Count}.");
        AssertEx.Equal(expected: 1, document.SchemaVersion);
    }

    [Test]
    public void Load_BundledSeedCatalog_EveryEntryHasUniqueIdAndValidRepoShape()
    {
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);

        var ids = document.Models.Select(m => m.Id).ToList();
        AssertEx.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in document.Models)
        {
            AssertEx.True(entry.GgufRepo.Contains('/', StringComparison.Ordinal), $"{entry.Id}: ggufRepo '{entry.GgufRepo}' must be 'owner/repo'.");
            AssertEx.True(entry.UseCases.Count > 0, $"{entry.Id}: useCases must be non-empty.");
        }
    }

    [Test]
    public void Load_BundledSeedCatalog_CoversAllSixUseCases()
    {
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);
        var coveredUseCases = document.Models.SelectMany(m => m.UseCases).ToHashSet(StringComparer.Ordinal);

        foreach (var useCase in new[] { "general", "coding", "reasoning", "chat", "multimodal", "embedding" })
        {
            AssertEx.True(coveredUseCases.Contains(useCase), $"no seed entry covers use-case '{useCase}'.");
        }
    }

    [Test]
    public void Load_BundledSeedCatalog_IncludesAtLeastOneMoeEntry()
    {
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);

        AssertEx.True(document.Models.Any(m => m.Moe && m.ActiveParamsB is > 0), "expected at least one MoE entry with a positive activeParamsB.");
    }
}
