namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapper tests for the benchmark-metrics projection. The harness measures the four speculative-decoding counters
///     and the context-token high watermark and persists them on the benchmark row, but they reached no wire surface
///     until they were projected here — an operator round would otherwise have to read them out of the per-column
///     encrypted SQLite file. The raw <c>/metrics</c> scrape and the diagnostics blob stay server-side and must NOT
///     appear on the DTO.
/// </summary>
public sealed class ModelFitMapperMetricsTests
{
    [Test]
    public void ToDto_ProjectsSpeculativeAndContextWatermarkMetrics()
    {
        var metrics = Metrics() with
        {
            ContextTokensHighWatermark = 24576d,
            SpeculativeDraftTokens = 900d,
            SpeculativeAcceptedTokens = 315d,
            SpeculativeVerificationSteps = 150d,
            SpeculativeAcceptanceRate = 0.35d
        };

        var dto = metrics.ToDto();

        AssertEx.Equal<double?>(expected: 24576d, dto.ContextTokensHighWatermark);
        AssertEx.Equal<double?>(expected: 900d, dto.SpeculativeDraftTokens);
        AssertEx.Equal<double?>(expected: 315d, dto.SpeculativeAcceptedTokens);
        AssertEx.Equal<double?>(expected: 150d, dto.SpeculativeVerificationSteps);
        AssertEx.Equal<double?>(expected: 0.35d, dto.SpeculativeAcceptanceRate);
    }

    [Test]
    public void ToDto_WhenSpeculationDidNotRun_LeavesTheProjectionsNull()
    {
        // A null acceptance rate is "no tokens were drafted", not a measured zero, so the mapper must never substitute
        // a value the harness declined to report.
        var dto = Metrics().ToDto();

        AssertEx.Null(dto.ContextTokensHighWatermark);
        AssertEx.Null(dto.SpeculativeDraftTokens);
        AssertEx.Null(dto.SpeculativeAcceptedTokens);
        AssertEx.Null(dto.SpeculativeVerificationSteps);
        AssertEx.Null(dto.SpeculativeAcceptanceRate);
    }

    [Test]
    public void ToDto_KeepsTheRawScrapeAndDiagnosticsServerSide()
    {
        var metrics = Metrics() with
        {
            RawJson = """{"llamacpp:n_tokens_max":24576}""",
            DiagnosticsJson = """{"speculative":{"draft":900}}"""
        };

        var dto = metrics.ToDto();

        var properties = dto.GetType().GetProperties().Select(property => property.Name).ToArray();
        AssertEx.Empty(properties.Where(name => name is "RawJson" or "DiagnosticsJson"));

        // Serialize the way the endpoint does, so the populated setup is load-bearing: the sentinels are unique to the
        // raw scrape and the diagnostics blob, so their absence proves nothing carried them onto the wire — a property
        // rename or an added passthrough would put them back.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        AssertEx.False(json.Contains("llamacpp:n_tokens_max", StringComparison.Ordinal));
        AssertEx.False(json.Contains("\"speculative\"", StringComparison.Ordinal));
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
