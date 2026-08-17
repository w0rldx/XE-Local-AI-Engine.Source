namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufVariantRecommender" /> tests: per-file quality tier + hardware fit verdict from a single mocked
///     free-VRAM probe, and the single-recommended-variant selection rule (best quality among Fits, then Tight, then
///     smallest when nothing fits, then the quality sweet-spot when no probe). The GPU-variant selector and VRAM probe
///     are mocked — no real hardware.
/// </summary>
public sealed class GgufVariantRecommenderTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Test]
    public async Task Annotate_EmptyList_ReturnsEmpty()
    {
        var recommender = Build(freeVramBytes: 12 * Gib);

        var result = await recommender.AnnotateAsync([], CancellationToken.None);

        AssertEx.Empty(result);
    }

    [Test]
    public async Task Annotate_PreservesInputOrder_AndAnnotatesEveryFile()
    {
        var recommender = Build(freeVramBytes: 12 * Gib);
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q6_K", 7 * Gib),
            RepoFile("Q8_0", 13 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(files.Length, result.Count);
        AssertEx.Equal("model-Q4_K_M.gguf", result[0].FileName);
        AssertEx.Equal("model-Q6_K.gguf", result[1].FileName);
        AssertEx.Equal("model-Q8_0.gguf", result[2].FileName);
    }

    [Test]
    public async Task Annotate_FitsPresent_RecommendsHighestQualityThatFits()
    {
        // free = 12 GiB. Q4_K_M (Balanced) and Q6_K (NearLossless) both fit; Q8_0 (13 GiB) won't fit. Among the fits the
        // highest tier (Q6_K) is recommended.
        var recommender = Build(freeVramBytes: 12 * Gib);
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q6_K", 7 * Gib),
            RepoFile("Q8_0", 13 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(result, "Q4_K_M"));
        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(result, "Q6_K"));
        AssertEx.Equal(GgufFitVerdict.WontFit, VerdictOf(result, "Q8_0"));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q6_K"));
    }

    [Test]
    public async Task Annotate_FitsTie_RecommendsLargerSize()
    {
        // Two NearLossless files both fit (free = 14 GiB) → tie broken by larger size (Q8_0 8 GiB over Q6_K 6 GiB).
        var recommender = Build(freeVramBytes: 14 * Gib);
        var files = new[]
        {
            RepoFile("Q6_K", 6 * Gib),
            RepoFile("Q8_0", 8 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q8_0"));
    }

    [Test]
    public async Task Annotate_NoneFitButSomeTight_RecommendsBestTight()
    {
        // free = 5 GiB. Q4_K_M 4.5 GiB fits the raw size but the headroom margin eats in → Tight; Q5_K_M 4.8 GiB also
        // Tight (higher tier). Nothing is a comfortable Fit → the best Tight (Q5_K_M, SweetSpot) is recommended.
        var recommender = Build(freeVramBytes: 5 * Gib);
        var files = new[]
        {
            RepoFile("Q4_K_M", (9 * Gib) / 2),
            RepoFile("Q5_K_M", (48 * Gib) / 10)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Tight, VerdictOf(result, "Q4_K_M"));
        AssertEx.Equal(GgufFitVerdict.Tight, VerdictOf(result, "Q5_K_M"));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q5_K_M"));
    }

    [Test]
    public async Task Annotate_AllWontFit_RecommendsSmallest()
    {
        // free = 2 GiB. Every file is larger → all WontFit → the smallest file is the least-bad pick.
        var recommender = Build(freeVramBytes: 2 * Gib);
        var files = new[]
        {
            RepoFile("Q6_K", 7 * Gib),
            RepoFile("Q4_K_M", 5 * Gib),
            RepoFile("Q8_0", 9 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.WontFit, VerdictOf(result, "Q4_K_M"));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q4_K_M"));
    }

    [Test]
    public async Task Annotate_NoProbe_VerdictsUnknown_AndRecommendsSweetSpot()
    {
        // No probe (free VRAM null) → every verdict is Unknown and the recommendation falls back to the quality
        // sweet-spot (Q5_K_M) over the balanced/near-lossless alternatives.
        var recommender = Build(freeVramBytes: null);
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q5_K_M", 5 * Gib),
            RepoFile("Q8_0", 9 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.True(result.All(static a => a.FitVerdict == GgufFitVerdict.Unknown));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q5_K_M"));
    }

    [Test]
    public async Task Annotate_NoProbe_NoSweetSpot_RecommendsBalanced()
    {
        // No probe and no sweet-spot file → fall back to the balanced default (Q4_K_M) over near-lossless/minimal.
        var recommender = Build(freeVramBytes: null);
        var files = new[]
        {
            RepoFile("Q8_0", 9 * Gib),
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q2_K", 2 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q4_K_M"));
    }

    [Test]
    public async Task Annotate_CarriesQualityTierPerFile()
    {
        var recommender = Build(freeVramBytes: null);
        var files = new[]
        {
            RepoFile("Q8_0", 9 * Gib),
            RepoFile("Q4_K_M", 4 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufQuantTier.NearLossless, TierOf(result, "Q8_0"));
        AssertEx.Equal(GgufQuantTier.Balanced, TierOf(result, "Q4_K_M"));
    }

    private static GgufVariantRecommender Build(long? freeVramBytes, GpuVariant variant = GpuVariant.Cuda)
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));

        var probe = Substitute.For<IProcessVramBudgetProbe>();
        probe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(freeVramBytes));

        return new GgufVariantRecommender(selector, probe, NullLogger<GgufVariantRecommender>.Instance);
    }

    [Test]
    public async Task Annotate_NeverRecommendsASpeculativeDecodingDrafter()
    {
        // The live gemma-4-12b shape: the MTP drafter is BOTH the smallest file and NearLossless-looking, so
        // under a plain fit-first / quality-first walk it wins outright — the ★ row would be a 0.4 GB non-chat model.
        var recommender = Build(freeVramBytes: 24 * Gib);
        var files = new[]
        {
            RepoFile("MTP-Q8_0", sizeBytes: 400L * 1024 * 1024),
            RepoFile("Q4_K_M", 7 * Gib),
            RepoFile("Q8_0", 11 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.False(IsRecommended(result, "MTP-Q8_0"), "A speculative-decoding drafter must never be the recommended variant.");
        AssertEx.True(IsRecommended(result, "Q8_0"), "The highest-quality fitting BASE quant is the recommendation.");
    }

    [Test]
    public async Task Annotate_DrafterOnlyRepository_RecommendsNothing()
    {
        var recommender = Build(freeVramBytes: 24 * Gib);
        var files = new[]
        {
            RepoFile("MTP-Q8_0", sizeBytes: 400L * 1024 * 1024),
            RepoFile("MTP-BF16", sizeBytes: 800L * 1024 * 1024)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(files.Length, result.Count);
        AssertEx.False(result.Any(static annotation => annotation.IsRecommended),
            "With nothing but drafters there is no recommendable variant.");
    }

    private static GgufRepoFile RepoFile(string quant, long sizeBytes)
    {
        return new GgufRepoFile($"model-{quant}.gguf",
            quant,
            sizeBytes,
            Sha256: null,
            Revision: "main",
            Architecture: null,
            QuantType: null,
            ParamCount: null,
            BlockCount: null,
            AttentionHeadCount: null,
            AttentionHeadCountKV: null,
            EmbeddingLength: null,
            ContextLength: null);
    }

    private static GgufFitVerdict VerdictOf(IReadOnlyList<GgufVariantAnnotation> result, string quant)
    {
        return result.Single(a => a.FileName == $"model-{quant}.gguf").FitVerdict;
    }

    private static GgufQuantTier TierOf(IReadOnlyList<GgufVariantAnnotation> result, string quant)
    {
        return result.Single(a => a.FileName == $"model-{quant}.gguf").QualityTier;
    }

    private static bool IsRecommended(IReadOnlyList<GgufVariantAnnotation> result, string quant)
    {
        return result.Single(a => a.FileName == $"model-{quant}.gguf").IsRecommended;
    }
}
