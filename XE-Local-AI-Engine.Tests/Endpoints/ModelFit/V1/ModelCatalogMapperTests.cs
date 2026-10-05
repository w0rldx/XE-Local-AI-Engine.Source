namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The catalog-info DTO's tested-model list, mapped from the ACTUAL bundled seed catalog.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelCatalogMapperTests
{
    private static ModelCatalogSnapshot BundledSnapshot()
    {
        return new ModelCatalogSnapshot
        {
            Document = ModelCatalogBundledLoader.Load(NullLogger.Instance),
            Source = ModelCatalogSource.Bundled,
            FetchedAtUtc = null,
            SourceUrl = null
        };
    }

    [Test]
    public void ToResponse_WithTheBundledSeed_ListsExactlyTheTestedEntriesSmallestFirst()
    {
        var snapshot = BundledSnapshot();
        GgufFitVerdict[] verdicts = [GgufFitVerdict.Fits, GgufFitVerdict.Tight, GgufFitVerdict.Fits, GgufFitVerdict.Unknown, GgufFitVerdict.WontFit];

        var response = snapshot.ToResponse(refreshSourceConfigured: false, verdicts);

        AssertEx.True(response.TestedModels.Select(model => model.Id)
                              .SequenceEqual(["granite-4.1-3b", "qwen3.5-4b", "qwen3.5-9b", "qwen3.8-27b", "qwen3.6-35b-a3b"]),
            $"unexpected tested list: {string.Join(", ", response.TestedModels.Select(model => model.Id))}");
        AssertEx.True(snapshot.TestedEntries().Select(entry => entry.Id).SequenceEqual(response.TestedModels.Select(model => model.Id)),
            "TestedEntries must give the response order, since the endpoints grade sizes in that order.");
        AssertEx.False(response.TestedModels.Any(model => model.Id == "qwen3.5-27b"), "an untested entry must not be listed.");

        var granite = response.TestedModels[0];
        AssertEx.Equal("Granite 4.1 3B", granite.DisplayName);
        AssertEx.Equal("IBM", granite.Publisher);
        AssertEx.Equal("unsloth/granite-4.1-3b-GGUF", granite.GgufRepo);
        AssertEx.Equal("apache-2.0", granite.License);
        AssertEx.Equal(3.4d, granite.TotalParamsB);
        AssertEx.Equal("Tool-capable small model with no thinking mode.", granite.Notes);
        AssertEx.Equal("Q4_K_M", granite.TestedQuant);
        AssertEx.Equal(2099502400L, granite.TestedSizeBytes);

        var moe = response.TestedModels[^1];
        AssertEx.Equal("UD-Q4_K_M", moe.TestedQuant);
        AssertEx.Equal(22134528992L, moe.TestedSizeBytes);

        // Each verdict lands on its own row, as the picker's wire value (the GgufFitVerdict enum name).
        AssertEx.True(response.TestedModels.Select(model => model.FitVerdict).SequenceEqual(["Fits", "Tight", "Fits", "Unknown", "WontFit"]),
            $"verdicts: {string.Join(", ", response.TestedModels.Select(model => model.FitVerdict))}");
    }

    [Test]
    public void ToResponse_WithFewerVerdictsThanTestedEntries_ReportsTheRestUnknown()
    {
        var response = BundledSnapshot().ToResponse(refreshSourceConfigured: true, [GgufFitVerdict.Fits]);

        AssertEx.Equal(expected: 5, response.TestedModels.Count);
        AssertEx.Equal("Fits", response.TestedModels[0].FitVerdict);
        AssertEx.True(response.TestedModels.Skip(1).All(model => model.FitVerdict == "Unknown"),
            $"verdicts: {string.Join(", ", response.TestedModels.Select(model => model.FitVerdict))}");
        AssertEx.True(response.RefreshSourceConfigured);
    }
}
