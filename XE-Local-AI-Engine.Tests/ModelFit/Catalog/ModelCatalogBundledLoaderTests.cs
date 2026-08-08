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
    public void Load_BundledSeedCatalog_DoesNotDeclareAUniformArchFloor()
    {
        // The arch gate is only as good as this column, and a UNIFORM column is the signature of nobody maintaining it.
        // That is not hypothetical: every one of the 41 entries shipped the same floor, including the Gemma 4 and
        // Qwen3.5 entries, while a live run on 2026-07-31 proved that floor's build cannot load those architectures at
        // all. The gate therefore failed OPEN in precisely the case it exists to catch.
        // This test does not, and cannot, assert that any particular floor is CORRECT — that is upstream llama.cpp
        // knowledge. It asserts the weaker, mechanically checkable property that the table is not a single repeated
        // value, which is what rots silently when a model is added by copying its neighbour.
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);

        var distinctFloors = document.Models
                                     .Select(model => model.MinLlamaCppTag)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToList();

        AssertEx.True(distinctFloors.Count > 1,
            "Every catalog entry declares the same minLlamaCppTag "
            + $"('{distinctFloors.FirstOrDefault()}'), which makes ModelCatalogArchGate a no-op. When you add a model "
            + "that needs a newer llama.cpp than the oldest entry, give it its own floor rather than copying the "
            + "neighbouring value.");
    }

    [Test]
    public void Load_BundledSeedCatalog_CurrentGenerationArchitecturesDeclareAFloorAboveTheOldBaseline()
    {
        // Pins the specific correction from the 2026-07-31 live evaluation: the Gemma 4 and Qwen3.5 families were
        // measured NOT to load on b9692 (the runtime the app's own remediation banner used to build), and to load on
        // b10201. Their floors must stay strictly above the old baseline, or the recommendation surface will once again
        // offer a user a model their runtime cannot open.
        var document = ModelCatalogBundledLoader.Load(NullLogger.Instance);

        var currentGeneration = document.Models
                                        .Where(model => model.Id.StartsWith("gemma4-", StringComparison.Ordinal)
                                                        || model.Id.StartsWith("qwen3.5-", StringComparison.Ordinal))
                                        .ToList();

        AssertEx.True(currentGeneration.Count > 0, "The seed must still carry the current-generation families.");

        foreach (var model in currentGeneration)
        {
            var floor = ModelCatalogArchGate.ParseBNumber(model.MinLlamaCppTag);
            AssertEx.True(floor is > 9692,
                $"Catalog entry '{model.Id}' declares minLlamaCppTag '{model.MinLlamaCppTag}', but this architecture "
                + "was measured not to load on b9692. Its floor must be above that baseline.");
        }
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

        foreach (var useCase in new[]
                 {
                     "general",
                     "coding",
                     "reasoning",
                     "chat",
                     "multimodal",
                     "embedding"
                 })
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
