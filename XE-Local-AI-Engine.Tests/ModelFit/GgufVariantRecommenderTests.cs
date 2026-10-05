namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit;
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
[Category(TestCategories.Unit)]
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
    public async Task Annotate_QuantizedFilesPresent_NeverRecommendsAnUnquantizedFloat()
    {
        // Everything fits (free = 24 GiB). BF16 shares Q8_0's NearLossless tier and is larger, so before the float exclusion it
        // won the size tie-break; with a quantized file on offer the pick moves to Q8_0 and BF16 stays listed with its verdict.
        var recommender = Build(freeVramBytes: 24 * Gib);
        var files = new[]
        {
            RepoFile("BF16", 6 * Gib),
            RepoFile("Q8_0", 3 * Gib),
            RepoFile("Q5_K_M", (5 * Gib) / 2),
            RepoFile("Q4_K_M", 2 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(result, "BF16"));
        AssertEx.Equal(GgufQuantTier.NearLossless, TierOf(result, "BF16"));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q8_0"));
    }

    [Test]
    public async Task Annotate_FloatOnlyRepository_StillRecommendsTheLargestFittingFloat()
    {
        // No quantized file on offer → the floats stay candidates and the existing rule applies: same tier, larger size wins.
        var recommender = Build(freeVramBytes: 24 * Gib);
        var files = new[]
        {
            RepoFile("BF16", 6 * Gib),
            RepoFile("F16", 6 * Gib + 1),
            RepoFile("F32", 12 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "F32"));
    }

    [Test]
    public async Task Annotate_UnslothDynamicQuant_IsEligibleOverAnUnquantizedFloat()
    {
        // A UD- quant is quantized, so it displaces BF16 even though BF16 grades the higher tier.
        var recommender = Build(freeVramBytes: 24 * Gib);
        var files = new[]
        {
            RepoFile("BF16", 6 * Gib),
            RepoFile("UD-Q4_K_XL", 2 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "UD-Q4_K_XL"));
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

    [Test]
    public async Task Annotate_KnowledgeCompanionReserve_ShrinksTheBudgetTheVerdictsUse()
    {
        // free = 12 GiB; Q6_K (7 GiB) needs 7 + max(15 %, 1 GiB) ≈ 8.05 GiB and fits. A 4 GiB companion reserve leaves 8 GiB:
        // Q6_K drops to Tight and the recommendation moves to Q4_K_M, which still fits.
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q6_K", 7 * Gib)
        };

        var unreserved = await Build(freeVramBytes: 12 * Gib).AnnotateAsync(files, CancellationToken.None);
        var reserved = await Build(freeVramBytes: 12 * Gib, companionReserveBytes: 4 * Gib).AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(unreserved, "Q6_K"));
        AssertEx.True(IsRecommended(unreserved, "Q6_K"));
        AssertEx.Equal(GgufFitVerdict.Tight, VerdictOf(reserved, "Q6_K"));
        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(reserved, "Q4_K_M"));
        AssertEx.True(IsRecommended(reserved, "Q4_K_M"));
    }

    [Test]
    public async Task Annotate_KnowledgeCompanionReserveLargerThanTheBudget_ClampsToWontFit_NotUnknown()
    {
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib)
        };

        var result = await Build(freeVramBytes: 2 * Gib, companionReserveBytes: 64 * Gib).AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.WontFit, VerdictOf(result, "Q4_K_M"),
            "A reserve beyond the budget must leave a known, tiny GPU budget, never an unknown one.");
    }

    [Test]
    public async Task Annotate_HandsTheReserveTheProbesLiveFigure_AsMeasuredFreeVram()
    {
        // The probe's figure is a live free measurement, so the reserve must see it (not the audit's cached one) and skip residents against it.
        var companionReserve = Substitute.For<IKnowledgeCompanionReserve>();

        await Build(freeVramBytes: 12 * Gib, companionReserve: companionReserve).AnnotateAsync([RepoFile("Q4_K_M", 4 * Gib)], CancellationToken.None);

        await companionReserve.Received(1)
                              .ResolveGpuBytesAsync(Arg.Is<HardwareProfile>(profile => profile.AvailableVramBytes == 12 * Gib), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Annotate_CpuModeNode_GradesAgainstAvailableRam_AndRecommendsByFit()
    {
        // A CPU-mode node with 12 GiB of available RAM and no VRAM budget. The 10.5 GiB file plus its headroom margin overshoots
        // the budget, so it is Tight, and the 13 GiB file is larger than the budget. The companion GPU reserve never applies here.
        var companionReserve = Substitute.For<IKnowledgeCompanionReserve>();
        var recommender = Build(freeVramBytes: null, variant: GpuVariant.Cpu, companionReserve: companionReserve, profile: CpuProfile(availableRamBytes: 12 * Gib));
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q6_K", (21 * Gib) / 2),
            RepoFile("Q8_0", 13 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Fits, VerdictOf(result, "Q4_K_M"));
        AssertEx.Equal(GgufFitVerdict.Tight, VerdictOf(result, "Q6_K"));
        AssertEx.Equal(GgufFitVerdict.WontFit, VerdictOf(result, "Q8_0"));
        AssertEx.Equal(1, result.Count(static a => a.IsRecommended));
        AssertEx.True(IsRecommended(result, "Q4_K_M"));
        await companionReserve.DidNotReceive().ResolveGpuBytesAsync(Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Annotate_CpuModeNodeWithUnknownRam_VerdictsUnknown_AndRecommendsSweetSpot()
    {
        var recommender = Build(freeVramBytes: null, variant: GpuVariant.Cpu, profile: CpuProfile(availableRamBytes: 0));
        var files = new[]
        {
            RepoFile("Q4_K_M", 4 * Gib),
            RepoFile("Q5_K_M", 5 * Gib),
            RepoFile("Q8_0", 9 * Gib)
        };

        var result = await recommender.AnnotateAsync(files, CancellationToken.None);

        AssertEx.True(result.All(static a => a.FitVerdict == GgufFitVerdict.Unknown));
        AssertEx.True(IsRecommended(result, "Q5_K_M"));
    }

    [Test]
    public async Task ClassifyAgainstProfile_GpuProfile_GradesAgainstCachedFreeVramLessTheReserve_WithoutTheProbe()
    {
        // Cached free VRAM 12 GiB less a 2 GiB companion reserve = 10 GiB. 4 GiB + 1 GiB margin fits; 9 GiB + 1.35 GiB margin
        // overshoots but the file alone fits (Tight); 11 GiB won't fit. Without the reserve the 9 GiB file would fit.
        var (recommender, probe, selector, companionReserve) = BuildForProfile(GpuProfile(freeVramBytes: 12 * Gib), companionReserveBytes: 2 * Gib);

        var result = await recommender.ClassifyAgainstProfileAsync([4 * Gib, 9 * Gib, 11 * Gib], CancellationToken.None);

        AssertEx.True(result.SequenceEqual([GgufFitVerdict.Fits, GgufFitVerdict.Tight, GgufFitVerdict.WontFit]), string.Join(", ", result));
        await companionReserve.Received(1)
                              .ResolveGpuBytesAsync(Arg.Is<HardwareProfile>(profile => profile.AvailableVramBytes == 12 * Gib), Arg.Any<CancellationToken>());
        await probe.DidNotReceive().TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await selector.DidNotReceive().SelectVariantAsync(Arg.Any<CancellationToken>());

        var (unreserved, _, _, _) = BuildForProfile(GpuProfile(freeVramBytes: 12 * Gib), companionReserveBytes: 0);
        AssertEx.Equal(GgufFitVerdict.Fits, (await unreserved.ClassifyAgainstProfileAsync([9 * Gib], CancellationToken.None))[0]);
    }

    [Test]
    public async Task ClassifyAgainstProfile_CpuModeProfile_GradesAgainstAvailableRam_WithoutTheReserve()
    {
        var (recommender, probe, _, companionReserve) = BuildForProfile(CpuProfile(availableRamBytes: 12 * Gib), companionReserveBytes: 4 * Gib);

        var result = await recommender.ClassifyAgainstProfileAsync([4 * Gib, (21 * Gib) / 2, 13 * Gib], CancellationToken.None);

        AssertEx.True(result.SequenceEqual([GgufFitVerdict.Fits, GgufFitVerdict.Tight, GgufFitVerdict.WontFit]), string.Join(", ", result));
        await companionReserve.DidNotReceive().ResolveGpuBytesAsync(Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
        await probe.DidNotReceive().TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClassifyAgainstProfile_UnknownHardware_IsUnknownForEverySize()
    {
        var (recommender, probe, _, _) = BuildForProfile(CpuProfile(availableRamBytes: 0), companionReserveBytes: 0);

        var result = await recommender.ClassifyAgainstProfileAsync([1 * Gib, 64 * Gib], CancellationToken.None);

        AssertEx.True(result.SequenceEqual([GgufFitVerdict.Unknown, GgufFitVerdict.Unknown]), string.Join(", ", result));
        await probe.DidNotReceive().TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClassifyAgainstProfile_ProfileReadFails_DegradesToUnknown_ButCallerCancellationPropagates()
    {
        var (recommender, _, _, _) = BuildForProfile(GpuProfile(freeVramBytes: 12 * Gib), companionReserveBytes: 0, auditFailure: new IOException("audit exploded"));

        var result = await recommender.ClassifyAgainstProfileAsync([4 * Gib], CancellationToken.None);

        AssertEx.Equal(GgufFitVerdict.Unknown, result[0]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (cancelled, _, _, _) = BuildForProfile(GpuProfile(freeVramBytes: 12 * Gib), companionReserveBytes: 0, auditFailure: new OperationCanceledException(cts.Token));
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => cancelled.ClassifyAgainstProfileAsync([4 * Gib], cts.Token));
    }

    private static HardwareProfile GpuProfile(long freeVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gib,
            AvailableRamBytes = 48 * Gib,
            VramBytes = 16 * Gib,
            AvailableVramBytes = freeVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gib
        };
    }

    // The profile path's collaborators are returned so a test can prove the live probe and variant selector were never touched.
    private static (GgufVariantRecommender Recommender, IProcessVramBudgetProbe Probe, IGpuVariantSelector Selector, IKnowledgeCompanionReserve Reserve)
        BuildForProfile(HardwareProfile profile, long companionReserveBytes, Exception? auditFailure = null)
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        var probe = Substitute.For<IProcessVramBudgetProbe>();
        var companionReserve = Substitute.For<IKnowledgeCompanionReserve>();
        companionReserve.ResolveGpuBytesAsync(Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(companionReserveBytes));
        var runtimeAudit = Substitute.For<IRuntimeDeviceAudit>();
        runtimeAudit.PeekEffectiveProfileAsync(Arg.Any<CancellationToken>())
                    .Returns(_ => auditFailure is null ? Task.FromResult<HardwareProfile?>(profile) : Task.FromException<HardwareProfile?>(auditFailure));

        return (new GgufVariantRecommender(selector, probe, companionReserve, runtimeAudit, NullLogger<GgufVariantRecommender>.Instance), probe, selector, companionReserve);
    }

    [Test]
    public async Task ClassifyAgainstProfile_NoCachedAudit_IsUnknownForEverySize_AndNeverComputesTheAudit()
    {
        var probe = Substitute.For<IProcessVramBudgetProbe>();
        var runtimeAudit = Substitute.For<IRuntimeDeviceAudit>();
        runtimeAudit.PeekEffectiveProfileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<HardwareProfile?>(null));
        var recommender = new GgufVariantRecommender(Substitute.For<IGpuVariantSelector>(), probe, Substitute.For<IKnowledgeCompanionReserve>(), runtimeAudit,
            NullLogger<GgufVariantRecommender>.Instance);

        var result = await recommender.ClassifyAgainstProfileAsync([1 * Gib, 64 * Gib], CancellationToken.None);

        AssertEx.True(result.SequenceEqual([GgufFitVerdict.Unknown, GgufFitVerdict.Unknown]), string.Join(", ", result));
        await runtimeAudit.DidNotReceive().GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await runtimeAudit.DidNotReceive().GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await probe.DidNotReceive().TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static HardwareProfile CpuProfile(long availableRamBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gib,
            AvailableRamBytes = availableRamBytes,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.None,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gib
        };
    }

    private static GgufVariantRecommender Build(long? freeVramBytes,
        GpuVariant variant = GpuVariant.Cuda,
        long companionReserveBytes = 0,
        IKnowledgeCompanionReserve? companionReserve = null,
        HardwareProfile? profile = null)
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));

        var probe = Substitute.For<IProcessVramBudgetProbe>();
        probe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(freeVramBytes));

        if (companionReserve is null)
        {
            companionReserve = Substitute.For<IKnowledgeCompanionReserve>();
            companionReserve.ResolveGpuBytesAsync(Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(companionReserveBytes));
        }

        // A stale cached free figure, which the probe's live one must replace.
        var runtimeAudit = Substitute.For<IRuntimeDeviceAudit>();
        runtimeAudit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(profile ?? new HardwareProfile
                    {
                        TotalRamBytes = 64 * Gib,
                        AvailableRamBytes = 48 * Gib,
                        VramBytes = 32 * Gib,
                        AvailableVramBytes = 30 * Gib,
                        VramKnown = true,
                        GpuVendor = GpuVendor.Nvidia,
                        GpuAccelAvailable = true,
                        CpuCores = 16,
                        FreeDiskBytes = 500 * Gib
                    }));

        return new GgufVariantRecommender(selector, probe, companionReserve, runtimeAudit, NullLogger<GgufVariantRecommender>.Instance);
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
        return new GgufRepoFile
        {
            FileName = $"model-{quant}.gguf",
            Quant = quant,
            SizeBytes = sizeBytes,
            Sha256 = null,
            Revision = "main",
            Architecture = null,
            QuantType = null,
            ParamCount = null,
            BlockCount = null,
            AttentionHeadCount = null,
            AttentionHeadCountKV = null,
            EmbeddingLength = null,
            ContextLength = null
        };
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
