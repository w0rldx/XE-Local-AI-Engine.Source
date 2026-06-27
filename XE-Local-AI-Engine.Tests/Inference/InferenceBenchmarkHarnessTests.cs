namespace XE_Local_AI_Engine.Tests.Inference;

using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceBenchmarkHarness.TryParsePromMetric" /> tests: the pure Prometheus-text parser pulls a named
///     counter out of a <c>/metrics</c> scrape (including a label set) and returns <see langword="null" /> when the
///     metric is absent — so throughput derivation is unit-testable without a live llama-server.
/// </summary>
public sealed class InferenceBenchmarkHarnessTests
{
    private const string MetricsScrape = """
                                         # HELP llamacpp:prompt_tokens_total Number of prompt tokens processed.
                                         # TYPE llamacpp:prompt_tokens_total counter
                                         llamacpp:prompt_tokens_total 1234
                                         # TYPE llamacpp:tokens_predicted_total counter
                                         llamacpp:tokens_predicted_total{slot="0"} 567.5
                                         """;

    [Test]
    public void PromMetricParser_ExtractsGauge()
    {
        // A bare counter line.
        AssertEx.Equal<double?>(1234d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:prompt_tokens_total"));

        // A counter carrying a label set is still extracted (the value follows the closing brace).
        AssertEx.Equal<double?>(567.5d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_total"));
    }

    [Test]
    public void PromMetricParser_ReturnsNull_WhenAbsent()
    {
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_seconds_total"));
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(text: null, "llamacpp:prompt_tokens_total"));
    }
}
