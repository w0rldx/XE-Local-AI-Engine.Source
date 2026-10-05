namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins every diagnostics-blob field a recommendation row surfaces, for each blob shape a persisted row can carry.</summary>
/// <remarks>
///     A full blob, a legacy blob without the newer keys, wrong-typed values, and a null, blank, malformed or
///     non-object blob. A blob that cannot be read degrades field by field to its default and never throws.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ModelFitMapperDiagnosticsTests
{
    private const string TrustedName = "unsloth/Qwen3-8B-GGUF:Q4_K_M";
    private const string UntrustedName = "someone/model-GGUF:Q4_K_M";

    [Test]
    public void ToResponse_WithAFullBlob_CopiesEveryField()
    {
        const string diagnostics = """
                                   {"release_date":"2025-03-12","is_trusted_publisher":false,"section":"recommended","tier":"balanced","tested":true,
                                    "catalog_id":"qwen3-8b","catalog_display_name":"Qwen3 8B","catalog_notes":"notes","expert_offload":true,
                                    "gpu_gb":6.5,"cpu_gb":1.25,"kv_quant":"Q8_0","kv_quant_estimated_gb":10.965,"kv_quant_headroom_gb":3.125,
                                    "kv_quant_fits":false,"kv_quant_requires_flash_attention":true,"kv_bytes_per_token":640,
                                    "kv_bytes_per_token_quant":"F16","attention_arch":"gqa"}
                                   """;

        var row = Map(diagnostics, TrustedName);

        AssertEx.Equal("2025-03-12", row.ReleaseDate);
        AssertEx.False(row.IsTrustedPublisher, "an explicit false in the blob wins over a trusted model name.");
        AssertEx.Equal("recommended", row.Section);
        AssertEx.Equal("balanced", row.Tier);
        AssertEx.True(row.Tested);
        AssertEx.Equal("qwen3-8b", row.CatalogId);
        AssertEx.Equal("Qwen3 8B", row.CatalogDisplayName);
        AssertEx.Equal("notes", row.CatalogNotes);
        AssertEx.True(row.ExpertsOffloaded);
        AssertEx.Equal(6.5d, row.GpuGb!.Value);
        AssertEx.Equal(1.25d, row.CpuGb!.Value);
        AssertEx.Equal("Q8_0", row.KvQuant);
        AssertEx.Equal(10.965d, row.KvQuantEstimatedGb!.Value);
        AssertEx.Equal(3.125d, row.KvQuantHeadroomGb!.Value);
        AssertEx.True(row.KvQuantFits == false);
        AssertEx.True(row.KvQuantRequiresFlashAttention == true);
        AssertEx.Equal(expected: 640L, row.KvBytesPerToken!.Value);
        AssertEx.Equal("F16", row.KvBytesPerTokenQuant);
        AssertEx.Equal("gqa", row.AttentionArch);
    }

    [Test]
    public void ToResponse_WithAnExplicitTrue_OverridesAnUntrustedModelName()
    {
        AssertEx.True(Map("""{"is_trusted_publisher":true}""", UntrustedName).IsTrustedPublisher);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("\"text\"")]
    [Arguments("{}")]
    [Arguments("""{"release_date":"2025-01-01"}""")]
    public void ToResponse_WithAnUnreadableOrLegacyBlob_DefaultsEveryFieldAndDerivesTrustFromTheName(string? diagnostics)
    {
        var trusted = Map(diagnostics, TrustedName);
        var untrusted = Map(diagnostics, UntrustedName);

        AssertEx.True(trusted.IsTrustedPublisher, "a blob without is_trusted_publisher derives trust from the model name.");
        AssertEx.False(untrusted.IsTrustedPublisher, "a blob without is_trusted_publisher derives trust from the model name.");
        AssertEx.Equal("explore", trusted.Section);
        AssertEx.False(trusted.ExpertsOffloaded);
        AssertEx.False(trusted.Tested, "a snapshot row written before the tested flag shipped reads as untested.");
        AssertEx.True(trusted.Tier is null && trusted.CatalogId is null && trusted.CatalogDisplayName is null && trusted.CatalogNotes is null);
        AssertEx.True(trusted.GpuGb is null && trusted.CpuGb is null);
        AssertEx.True(trusted.KvQuant is null && trusted.KvQuantEstimatedGb is null && trusted.KvQuantHeadroomGb is null);
        AssertEx.True(trusted.KvQuantFits is null && trusted.KvQuantRequiresFlashAttention is null);
        AssertEx.True(trusted.KvBytesPerToken is null && trusted.KvBytesPerTokenQuant is null && trusted.AttentionArch is null);
    }

    [Test]
    public void ToResponse_WithWrongTypedValues_ReadsEachAsItsDefault()
    {
        const string diagnostics = """
                                   {"release_date":20250312,"is_trusted_publisher":"yes","section":7,"tier":null,"tested":"true","expert_offload":"true",
                                    "gpu_gb":"6.5","kv_quant_fits":1,"kv_bytes_per_token":640.5,"kv_quant_estimated_gb":1e400}
                                   """;

        var row = Map(diagnostics, TrustedName);

        AssertEx.True(row.ReleaseDate is null);
        AssertEx.True(row.IsTrustedPublisher, "a non-boolean is_trusted_publisher falls back to the name-derived signal.");
        AssertEx.Equal("explore", row.Section);
        AssertEx.True(row.Tier is null);
        AssertEx.False(row.Tested);
        AssertEx.False(row.ExpertsOffloaded);
        AssertEx.True(row.GpuGb is null);
        AssertEx.True(row.KvQuantFits is null);
        AssertEx.True(row.KvBytesPerToken is null, "a fractional KV-per-token figure reads as null.");
        AssertEx.True(row.KvQuantEstimatedGb is { } estimate && double.IsPositiveInfinity(estimate), "an out-of-range number reads as infinity, as JsonElement.TryGetDouble reports it.");
    }

    [Test]
    public void ToResponse_WithADuplicatedKey_ReadsTheLastValue()
    {
        AssertEx.Equal("second", Map("""{"section":"first","section":"second"}""", TrustedName).Section);
    }

    private static ModelFitRecommendationResponse Map(string? diagnosticsJson, string modelName)
    {
        var record = new ModelFitRecommendationRecord
        {
            Id = Guid.NewGuid(),
            SnapshotId = Guid.NewGuid(),
            Rank = 1,
            ModelName = modelName,
            ProviderModelName = null,
            Score = 90d,
            FitLevel = "gpu",
            RunMode = null,
            Quantization = "Q4_K_M",
            EstimatedTokensPerSecond = null,
            RequiredRamMb = 12000d,
            RequiredVramMb = 12000d,
            ContextTokens = 8192,
            IsInstalled = false,
            PullModelName = null,
            DiagnosticsJson = diagnosticsJson
        };
        var view = new ModelFitLatestRecommendationsView
        {
            SnapshotId = Guid.NewGuid(),
            Status = ModelFitRunStatus.Succeeded,
            ApprovedImageId = "advisor",
            UseCase = "coding",
            ProviderName = "advisor",
            CompletedAtUtc = 0L,
            Recommendations =
            [
                new ModelFitRecommendationView
                {
                    Record = record,
                    Diagnostics = ModelFitRecommendationDiagnostics.Parse(record.DiagnosticsJson, record.ModelName)
                }
            ],
            SkippedCatalogEntries = []
        };

        return view.ToResponse().Recommendations[0];
    }

    [Test]
    public void ToResponse_CopiesTheSkippedCatalogEntries_AndACacheMissHasNone()
    {
        var view = new ModelFitLatestRecommendationsView
        {
            SnapshotId = Guid.NewGuid(),
            Status = ModelFitRunStatus.Succeeded,
            ApprovedImageId = "advisor",
            UseCase = "coding",
            ProviderName = "advisor",
            CompletedAtUtc = 0L,
            Recommendations = [],
            SkippedCatalogEntries = ["Gemma 4 12B", "Qwen3 8B"]
        };

        AssertEx.Equal("Gemma 4 12B|Qwen3 8B", string.Join('|', view.ToResponse().SkippedCatalogEntries));
        AssertEx.Empty(ModelFitMapper.EmptyCache().SkippedCatalogEntries);
    }
}
