namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A KL-divergence figure reaches a reader only while the digest it was measured under is the one the project's
///     current settings recompute. The API WITHHOLDS the numbers rather than sending them flagged: a number a reader
///     can still see is a number they will still compare, and one measured over a different corpus, chunk count or
///     base model means something different from the one beside it.
/// </summary>
public sealed class BenchmarkFidelityDisplayGateTests
{
    private static readonly string BaseFingerprint = "v1:" + new string('a', 64);
    private static readonly string CorpusSha = new('b', 64);

    [Test]
    public void ToFidelity_WhenTheDigestMatches_ServesTheNumbers()
    {
        var digest = Digest(chunks: 200);

        var response = AssertEx.NotNull(RunWith(digest).ToFidelity(digest));

        AssertEx.Equal(BenchmarkFidelityKldStates.Ok, response.KldState);
        AssertEx.Equal<double?>(0.030165, response.KldMean);
        AssertEx.Equal<double?>(0.388019, response.KldP99);
        AssertEx.Equal<double?>(0.91529, response.TopTokenAgreement);
        AssertEx.Equal<double?>(6.7983, response.PerplexityMean, "Perplexity carries its own corpus id and is never gated on the KLD digest.");
    }

    [Test]
    public void ToFidelity_WhenTheChunkCountChangedSinceTheMeasurement_RendersStaleAndSendsNoNumber()
    {
        // Same base model, same corpus — only the chunk count moved. This is the case a fingerprint-only gate passed,
        // and p99 in particular is strongly chunk-count dependent.
        var response = AssertEx.NotNull(RunWith(Digest(chunks: 200)).ToFidelity(Digest(chunks: 50)));

        AssertEx.Equal(BenchmarkFidelityKldStates.Stale, response.KldState);
        AssertEx.Null(response.KldMean, "A stale figure is withheld, not greyed.");
        AssertEx.Null(response.KldP99);
        AssertEx.Null(response.TopTokenAgreement);
        AssertEx.Equal<double?>(6.7983, response.PerplexityMean, "Perplexity is unaffected — it was measured over the same bytes at the same window.");
    }

    [Test]
    public void ToFidelity_WhenTheCorpusOrTheBaseModelChanged_AlsoRendersStale()
    {
        var measured = Digest(chunks: 200);

        var otherCorpus = BenchmarkKldCacheKey.Create(BaseFingerprint, new string('c', 64), 200).Digest;
        var otherBase = BenchmarkKldCacheKey.Create("v1:" + new string('d', 64), CorpusSha, 200).Digest;

        AssertEx.Equal(BenchmarkFidelityKldStates.Stale, AssertEx.NotNull(RunWith(measured).ToFidelity(otherCorpus)).KldState);
        AssertEx.Equal(BenchmarkFidelityKldStates.Stale, AssertEx.NotNull(RunWith(measured).ToFidelity(otherBase)).KldState);
    }

    [Test]
    public void ToFidelity_WhenTheProjectDoesNotMeasureDivergence_StillRendersStaleRatherThanANumber()
    {
        // A project that turned KLD off expects no digest at all. The historical numbers do not become comparable by
        // the expectation disappearing.
        var response = AssertEx.NotNull(RunWith(Digest(chunks: 200)).ToFidelity(expectedKldBaseLogitsDigest: null));

        AssertEx.Equal(BenchmarkFidelityKldStates.Stale, response.KldState);
        AssertEx.Null(response.KldMean);
    }

    [Test]
    public void ToFidelity_WhenNothingWasEverMeasured_IsAbsentRatherThanEmpty()
    {
        AssertEx.Null(Run(fidelity: null).ToFidelity(Digest(chunks: 200)), "No measurement is no block, not a block of nulls.");

        var perplexityOnly = AssertEx.NotNull(Run(new BenchmarkRunFidelity("succeeded", Guid.NewGuid(), 6.7983, 0.07405, 200, 512,
                                                      "wikitext2-raw-test@abc", null, null, null, null, null, null))
                                              .ToFidelity(Digest(chunks: 200)));
        AssertEx.Equal(BenchmarkFidelityKldStates.None, perplexityOnly.KldState, "A perplexity-only run measured no divergence — that is not staleness.");
    }

    private static string Digest(int chunks) =>
        BenchmarkKldCacheKey.Create(BaseFingerprint, CorpusSha, chunks).Digest;

    private static BenchmarkRunRecord RunWith(string measuredDigest) =>
        Run(new BenchmarkRunFidelity("succeeded",
            Guid.NewGuid(),
            PerplexityMean: 6.7983,
            PerplexityStdErr: 0.07405,
            PerplexityChunks: 200,
            PerplexityContextTokens: 512,
            PerplexityCorpusId: "wikitext2-raw-test@abc",
            KldMean: 0.030165,
            KldP99: 0.388019,
            TopTokenAgreement: 0.91529,
            KldBaseFingerprint: BaseFingerprint,
            KldBaseLogitsDigest: measuredDigest,
            ErrorMessage: null));

    private static BenchmarkRunRecord Run(BenchmarkRunFidelity? fidelity) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            new byte[] { 1 },
            "quant.gguf",
            LocalModelOrigin.Imported,
            "v1:" + new string('e', 64),
            "Agent",
            1,
            4096,
            BenchmarkPrimaryStatus.Succeeded,
            4096,
            10,
            5,
            500,
            null,
            1,
            null,
            null,
            1,
            1,
            1,
            1,
            1,
            Fidelity: fidelity);
}
