namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapper tests for the benchmark-metrics projection. The raw <c>/metrics</c> scrape, diagnostics blob, and internal
///     timing samples stay server-side and must NOT appear on the DTO.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelFitMapperMetricsTests
{
    private static readonly JsonSerializerOptions WebSerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void ToDto_ProjectsContextWatermarkCacheAndRunsWithoutSpeculativeFields()
    {
        var metrics = Metrics() with
        {
            ContextTokensHighWatermark = 24576d,
            CacheHitRate = 0.35d
        };

        var dto = metrics.ToDto();

        AssertEx.Equal<double?>(expected: 24576d, dto.ContextTokensHighWatermark);
        AssertEx.Equal<double?>(expected: 0.35d, dto.CacheHitRate);
        AssertEx.Equal(5, dto.Runs);

        var json = JsonSerializer.Serialize(dto, WebSerializerOptions);
        AssertEx.False(json.Contains("speculativeDraftTokens", StringComparison.Ordinal));
        AssertEx.False(json.Contains("speculativeAcceptedTokens", StringComparison.Ordinal));
        AssertEx.False(json.Contains("speculativeVerificationSteps", StringComparison.Ordinal));
        AssertEx.False(json.Contains("speculativeAcceptanceRate", StringComparison.Ordinal));
    }

    [Test]
    public void ToDto_KeepsTheRawScrapeAndDiagnosticsServerSide()
    {
        var metrics = Metrics() with
        {
            RawJson = """{"llamacpp:n_tokens_max":24576}""",
            DiagnosticsJson = """{"speculative_draft_sentinel_900":{"draft":900}}"""
        };

        var dto = metrics.ToDto();

        var properties = dto.GetType().GetProperties().Select(property => property.Name).ToArray();
        AssertEx.Empty(properties.Where(name => name is "RawJson" or "DiagnosticsJson"));

        // Serialize with library defaults (the sentinels are quote-free VALUE fragments, so name casing and the encoder
        // do not matter): the populated setup is load-bearing because the sentinels are unique to the raw scrape and
        // the diagnostics blob, so their absence proves nothing carried them onto the wire — a property rename or an
        // added passthrough would put them back. A quoted sentinel would never match: a leaked string property is
        // serialized with its inner quotes escaped.
        var json = JsonSerializer.Serialize(dto);
        AssertEx.False(json.Contains("llamacpp:n_tokens_max", StringComparison.Ordinal));
        AssertEx.False(json.Contains("speculative_draft_sentinel_900", StringComparison.Ordinal));
    }

    private static InferenceBenchmarkMetrics Metrics()
    {
        return new InferenceBenchmarkMetrics(Success: true,
            FailureReason: null,
            TokensPerSecond: 42d,
            PpTokensPerSecond: 800d,
            TtftMs: 310d,
            TotalLatencyMs: 12000d,
            CacheHitRate: 0.5d,
            ToolLoopMs: 900d,
            VramLoadBytes: 20_000_000_000,
            VramAfterBytes: 19_000_000_000,
            Runs: 5,
            RawJson: null,
            Role: "Chat");
    }
}
