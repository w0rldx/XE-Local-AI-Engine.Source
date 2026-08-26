namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The parser reads what the binary actually prints. Both fixtures below are verbatim captures from llama.cpp
///     b10201 on an RTX 5090 — not paraphrases of the upstream README, which describes a different output in three
///     places that would each have failed silently as "unparseable" rather than loudly as a bug.
/// </summary>
public sealed class BenchmarkPerplexityOutputParserTests
{
    /// <summary>A plain perplexity run, tail-trimmed. The log prefix in front of the line is real and load-bearing.</summary>
    private const string PlainPerplexityOutput = """
        0.24.342.828 I perplexity: calculating perplexity over 200 chunks, n_ctx=512, batch_size=2048, n_seq=4
        0.31.519.491 I perplexity: 7.18 seconds per pass - ETA 5.97 minutes
        [1]4.3196,[2]6.0868,[3]5.7473,[4]5.7262,[5]5.7436,[199]6.7956,[200]6.7983,
        1.12.579.214 I Final estimate: PPL = 6.7983 +/- 0.07405
        """;

    /// <summary>A <c>--kl-divergence</c> run. Note there is no <c>Final estimate</c> line anywhere in it.</summary>
    private const string KlDivergenceOutput = """
        ====== Perplexity statistics ======
        Mean PPL(Q)                   :   5.886524 ±   0.398426
        Mean PPL(base)                :   5.771204 ±   0.388860
        Cor(ln(PPL(Q)), ln(PPL(base))):  99.31%
        Mean ln(PPL(Q)/PPL(base))     :   0.019785 ±   0.007938

        ====== KL divergence statistics ======
        Mean    KLD:   0.030165 ±   0.002043
        Maximum KLD:   0.885166
        99.9%   KLD:   0.821833
        99.0%   KLD:   0.388019
        95.0%   KLD:   0.114668
        Median  KLD:   0.010790
        Minimum KLD:  -0.000002

        ====== Token probability statistics ======
        RMS Δp    :  5.243 ± 0.394 %
        Same top p: 91.529 ± 0.780 %
        """;

    [Test]
    public void TryParsePerplexity_OnARealPlainRun_ReadsTheMeanAndItsStandardError()
    {
        var reading = AssertEx.NotNull(BenchmarkPerplexityOutputParser.TryParsePerplexity(PlainPerplexityOutput));

        AssertEx.Equal(expected: 6.7983, reading.Mean);
        AssertEx.Equal(expected: 0.07405, reading.StandardError);
    }

    /// <summary>
    ///     The trap this test exists for: a KL-divergence run reports its perplexity as <c>Mean PPL(Q)</c> and prints
    ///     no final-estimate line. A parser that looked only for the plain shape would discard a measurement that
    ///     succeeded, and the failure would read as "unparseable output" rather than as a missing pattern.
    /// </summary>
    [Test]
    public void TryParsePerplexity_OnAKlDivergenceRun_ReadsMeanPplOfTheQuant()
    {
        AssertEx.False(KlDivergenceOutput.Contains("Final estimate", StringComparison.Ordinal),
            "The fixture must keep the property that makes this case real: a KLD run prints no final estimate.");

        var reading = AssertEx.NotNull(BenchmarkPerplexityOutputParser.TryParsePerplexity(KlDivergenceOutput));

        AssertEx.Equal(expected: 5.886524, reading.Mean);
        AssertEx.Equal(expected: 0.398426, reading.StandardError, "The statistics block separates value from error with U+00B1, not '+/-'.");
    }

    [Test]
    public void TryParseKld_ReadsTheMeanTheP99AndTheAgreementAsAFraction()
    {
        var reading = AssertEx.NotNull(BenchmarkPerplexityOutputParser.TryParseKld(KlDivergenceOutput));

        AssertEx.Equal(expected: 0.030165, reading.Mean);
        AssertEx.Equal<double?>(0.388019, reading.P99, "The p99 row is '99.0%   KLD', distinct from the 99.9% row just above it.");

        // Printed as 91.529 %, stored as a fraction, so no reader has to know which of the two a column holds. The
        // tolerance is for the divide-by-100, not for the parse: 91.529 / 100.0 is not bit-identical to the literal.
        AssertEx.True(reading.TopTokenAgreement is { } agreement && Math.Abs(agreement - 0.91529) < 1e-12,
            $"Agreement is printed as 'Same top p', with no word 'agreement' anywhere; got {reading.TopTokenAgreement}.");
    }

    [Test]
    public void TryParse_OnOutputWithoutTheExpectedBlock_ReturnsNullRatherThanZero()
    {
        // Fail closed: the caller turns null into a FAILED measurement. A zero here would be a real perplexity and a
        // perfect KL divergence — the two most flattering numbers the axis can report.
        AssertEx.Null(BenchmarkPerplexityOutputParser.TryParsePerplexity("llama_model_load: error loading model"));
        AssertEx.Null(BenchmarkPerplexityOutputParser.TryParsePerplexity(null));
        AssertEx.Null(BenchmarkPerplexityOutputParser.TryParseKld(PlainPerplexityOutput), "A plain run measured no divergence, so it reports none.");
        AssertEx.Null(BenchmarkPerplexityOutputParser.TryParseKld(null));
    }

    [Test]
    public void Tail_BoundsTheOperatorVisibleFailureReason()
    {
        var output = string.Join('\n', Enumerable.Range(0, 500).Select(index => $"line {index} of noise"));

        var tail = BenchmarkPerplexityOutputParser.Tail(output);

        AssertEx.True(tail.Length <= 1024, $"The reason quoted to an operator must stay bounded; got {tail.Length}.");
        AssertEx.True(tail.EndsWith("line 499 of noise", StringComparison.Ordinal), "It must be the TAIL — the failure is at the end, not the start.");
        AssertEx.Equal(string.Empty, BenchmarkPerplexityOutputParser.Tail(null));
    }
}
