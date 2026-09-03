namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapper tests for the advisory quantized-KV fields on the latest-recommendations projection. The advisory rides
///     the persisted diagnostics blob (<c>kv_quant*</c> keys) and is extracted like the other catalog-lane fields; a
///     blob without the keys (explore row / pre-advisory snapshot) must yield all-null advisory fields, and the row's
///     primary fit fields must never be affected by the advisory's presence.
/// </summary>
public sealed class ModelFitMapperKvQuantTests
{
    private static ModelFitRecommendationRecord CreateRecord(string? diagnosticsJson)
    {
        return new ModelFitRecommendationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            Rank: 1,
            ModelName: "repo/model:Q4_K_M",
            ProviderModelName: null,
            Score: 90d,
            FitLevel: "gpu",
            RunMode: null,
            Quantization: "Q4_K_M",
            EstimatedTokensPerSecond: null,
            RequiredRamMb: 12000d,
            RequiredVramMb: 12000d,
            ContextTokens: 8192,
            IsInstalled: false,
            PullModelName: null,
            DiagnosticsJson: diagnosticsJson);
    }

    private static ModelFitLatestRecommendationsView CreateView(ModelFitRecommendationRecord record)
    {
        return new ModelFitLatestRecommendationsView(Guid.NewGuid(),
            ModelFitRunStatus.Succeeded,
            ApprovedImageId: "advisor",
            UseCase: "coding",
            ProviderName: "advisor",
            CompletedAtUtc: 0L,
            Recommendations: [record]);
    }

    [Test]
    public void ToResponse_WhenDiagnosticsCarryKvQuantAdvisory_ExtractsAllAdvisoryFields()
    {
        const string diagnostics = """
                                   {"section":"recommended","kv_quant":"Q8_0","kv_quant_estimated_gb":10.965,
                                    "kv_quant_headroom_gb":3.125,"kv_quant_fits":true,"kv_quant_requires_flash_attention":true}
                                   """;
        var view = CreateView(CreateRecord(diagnostics));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.Equal("Q8_0", row.KvQuant);
        AssertEx.Equal(10.965d, row.KvQuantEstimatedGb!.Value);
        AssertEx.Equal(3.125d, row.KvQuantHeadroomGb!.Value);
        AssertEx.True(row.KvQuantFits == true, "the advisory fits flag must round-trip from the diagnostics blob.");
        AssertEx.True(row.KvQuantRequiresFlashAttention == true, "the flash-attention requirement must round-trip from the diagnostics blob.");
    }

    [Test]
    public void ToResponse_WhenDiagnosticsLackKvQuantKeys_YieldsNullAdvisoryFields()
    {
        var view = CreateView(CreateRecord("""{"section":"explore","release_date":"2025-03-12"}"""));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.True(row.KvQuant is null, "a blob without kv_quant keys must yield a null advisory label.");
        AssertEx.True(row.KvQuantEstimatedGb is null, "a blob without kv_quant keys must yield a null advisory estimate.");
        AssertEx.True(row.KvQuantHeadroomGb is null, "a blob without kv_quant keys must yield a null advisory headroom.");
        AssertEx.True(row.KvQuantFits is null, "a blob without kv_quant keys must yield a null advisory fits flag.");
        AssertEx.True(row.KvQuantRequiresFlashAttention is null, "a blob without kv_quant keys must yield a null flash-attention flag.");
    }

    [Test]
    public void ToResponse_WhenAdvisoryPresent_PrimaryFitFieldsAreUnchanged()
    {
        const string diagnostics = """{"kv_quant":"Q8_0","kv_quant_estimated_gb":10.0,"kv_quant_headroom_gb":4.0,"kv_quant_fits":true,"kv_quant_requires_flash_attention":true}""";
        var view = CreateView(CreateRecord(diagnostics));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.Equal(12000d, row.RequiredVramMb!.Value);
        AssertEx.Equal(12000d, row.RequiredRamMb!.Value);
        AssertEx.Equal("gpu", row.FitLevel);
        AssertEx.Equal(90d, row.Score);
    }

    [Test]
    public void ToResponse_WhenDiagnosticsCarryKvPerTokenFields_ExtractsThemWithTheirQuantAndArch()
    {
        const string diagnostics = """
                                   {"section":"recommended","kv_bytes_per_token":640,"kv_bytes_per_token_quant":"Q8_0",
                                    "attention_arch":"mla"}
                                   """;
        var view = CreateView(CreateRecord(diagnostics));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.Equal(expected: 640L, row.KvBytesPerToken!.Value);
        AssertEx.Equal("Q8_0", row.KvBytesPerTokenQuant);
        AssertEx.Equal("mla", row.AttentionArch);
    }

    [Test]
    public void ToResponse_WhenDiagnosticsLackKvPerTokenKeys_YieldsNullPerTokenFields()
    {
        var view = CreateView(CreateRecord("""{"section":"explore","release_date":"2025-03-12"}"""));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.True(row.KvBytesPerToken is null, "a pre-existing snapshot row must read the KV-per-token figure as null.");
        AssertEx.True(row.KvBytesPerTokenQuant is null, "a pre-existing snapshot row must read the KV-per-token quant as null.");
        AssertEx.True(row.AttentionArch is null, "a pre-existing snapshot row must read the attention tag as null.");
    }

    [Test]
    public void ToResponse_WhenDiagnosticsMalformed_YieldsNullAdvisoryFields()
    {
        var view = CreateView(CreateRecord("{not json"));

        var response = view.ToResponse();

        var row = response.Recommendations[0];
        AssertEx.True(row.KvQuant is null, "a malformed blob must yield a null advisory, never throw.");
        AssertEx.True(row.KvQuantFits is null, "a malformed blob must yield a null advisory fits flag, never throw.");
    }
}
