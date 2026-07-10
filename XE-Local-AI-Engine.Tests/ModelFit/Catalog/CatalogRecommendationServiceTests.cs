namespace XE_Local_AI_Engine.Tests.ModelFit.Catalog;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="CatalogRecommendationService" />: use-case + arch-tag filtering, the Recommended/CanRun split, the
///     tier → fit-class → quant → recency → id ranking order (plan §7), and the MoE expert-offload path.
/// </summary>
public sealed class CatalogRecommendationServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public async Task BuildRecommendationsAsync_FiltersByUseCase()
    {
        var entries = new[]
        {
            Entry("chat-model", useCases: ["chat"], tier: "S"),
            Entry("coding-model", useCases: ["coding"], tier: "S")
        };
        var discovery = DiscoveryReturning(entries, paramCountB: 1);
        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync("coding", "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        var allIds = result.Recommended.Concat(result.CanRun).Select(c => c.Entry.Id).ToList();
        AssertEx.True(allIds.Contains("coding-model"));
        AssertEx.False(allIds.Contains("chat-model"), "a chat-only entry must not appear for the coding use-case.");
    }

    [Test]
    public async Task BuildRecommendationsAsync_ExcludesEntry_WhenArchGateFails()
    {
        var entries = new[] { Entry("too-new", useCases: ["general"], tier: "S", minLlamaCppTag: "b9999") };
        var discovery = DiscoveryReturning(entries, paramCountB: 1);
        var service = BuildService(entries, discovery, installedTag: "b9000");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        AssertEx.Empty(result.Recommended);
        AssertEx.Empty(result.CanRun);
    }

    [Test]
    public async Task BuildRecommendationsAsync_SplitsRecommendedVsCanRun()
    {
        // A roomy 64GB budget: a 1B model fits with headroom at Q4_K_M (Recommended); a huge 60B model fits only at a
        // sub-Q4 quant (CanRun).
        var entries = new[]
        {
            Entry("small", useCases: ["general"], tier: "A"),
            Entry("huge", useCases: ["general"], tier: "A")
        };
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.InspectRepoAsync("org/small-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/small-GGUF", File("Q4_K_M", paramCountB: 1))));
        discovery.InspectRepoAsync("org/huge-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/huge-GGUF", File("Q3_K_M", paramCountB: 60))));

        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        AssertEx.ContainsSingle(result.Recommended, c => c.Entry.Id == "small");
        AssertEx.ContainsSingle(result.CanRun, c => c.Entry.Id == "huge");
    }

    [Test]
    public async Task BuildRecommendationsAsync_OrdersByTierThenQuantThenRecencyThenId()
    {
        // Two S-tier entries with identical fit-class and quant: the newer releaseDate must rank first even though its
        // id ("zzz") loses the ordinal id tie-break — proving the recency step outranks the id tie-break. A B-tier
        // entry with an otherwise-better fit must still rank LAST — proving tier dominates.
        var entries = new[]
        {
            Entry("b-tier", useCases: ["general"], tier: "B", releaseDate: "2026-06-01"),
            Entry("aaa", useCases: ["general"], tier: "S", releaseDate: "2020-01-01"),
            Entry("zzz", useCases: ["general"], tier: "S", releaseDate: "2026-01-01")
        };
        var discovery = DiscoveryReturning(entries, paramCountB: 1);
        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        var ids = result.Recommended.Select(c => c.Entry.Id).ToList();
        AssertEx.Equal("zzz", ids[0]);
        AssertEx.Equal("aaa", ids[1]);
        AssertEx.Equal("b-tier", ids[2]);
    }

    [Test]
    public async Task BuildRecommendationsAsync_MoeEntry_ResolvesToExpertOffload_WhenResidentDoesNotFit()
    {
        // A 30B-A3B MoE model that does NOT fit resident in a 16GB budget but DOES fit with experts offloaded to RAM.
        var entries = new[] { Entry("moe-model", useCases: ["general"], tier: "S", totalParamsB: 30, activeParamsB: 3, moe: true) };
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.InspectRepoAsync("org/moe-model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/moe-model-GGUF", File("Q4_K_M", paramCountB: 30))));

        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null,
            "Q4_K_M",
            ctxTarget: 8192,
            GpuProfile(16 * Gb),
            Empty,
            CancellationToken.None);

        var all = result.Recommended.Concat(result.CanRun).ToList();
        AssertEx.ContainsSingle(all, c => c.Entry.Id == "moe-model");
        var candidate = all.Single(c => c.Entry.Id == "moe-model");
        AssertEx.Equal(MoeFitVerdict.FitsWithExpertOffload, candidate.Estimate.MoeVerdict);
        AssertEx.True(candidate.Estimate.ExpertsOffloaded);
    }

    [Test]
    public async Task BuildRecommendationsAsync_MarksInstalledFromInstalledKeys()
    {
        var entries = new[] { Entry("installed-model", useCases: ["general"], tier: "S") };
        var discovery = DiscoveryReturning(entries, paramCountB: 1);
        var service = BuildService(entries, discovery, installedTag: "b9692");
        var installed = new HashSet<string>(StringComparer.Ordinal) { "org/installed-model-GGUF:Q4_K_M" };

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), installed, CancellationToken.None);

        AssertEx.True(result.Recommended.Single(c => c.Entry.Id == "installed-model").IsInstalled);
    }

    [Test]
    public async Task BuildRecommendationsAsync_CompleteMetadata_ProducesQ8KvQuantAdvisoryBelowFp16()
    {
        // Complete header metadata + a model that fits at fp16: the advisory is present, computed at Q8_0, needs flash
        // attention, and its estimate is strictly below the fp16 estimate (the quantized KV cache is the only difference).
        var entries = new[] { Entry("fits-model", useCases: ["general"], tier: "S") };
        var discovery = DiscoveryReturning(entries, paramCountB: 1);
        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        var candidate = result.Recommended.Single(c => c.Entry.Id == "fits-model");
        var advisory = AssertEx.NotNull(candidate.KvQuantAdvisory);
        AssertEx.Equal(KvCacheQuant.Q8_0, advisory.Quant);
        AssertEx.True(advisory.RequiresFlashAttention, "a quantized KV cache always requires flash attention.");
        AssertEx.True(advisory.Fits, "the advisory should still fit when the fp16 estimate fits with headroom.");
        AssertEx.True(advisory.EstimatedBytes < candidate.Estimate.EstimatedBytes,
            "the Q8_0 KV cache must lower the estimate below the fp16 estimate.");
    }

    [Test]
    public async Task BuildRecommendationsAsync_IncompleteMetadata_OmitsKvQuantAdvisory()
    {
        // A file whose header lacks BlockCount (so the KV term is 0): the candidate still surfaces (param-count drives the
        // weights term and it fits), but the KV-quant advisory is suppressed because the "savings" would be nil/misleading.
        var entries = new[] { Entry("no-blocks-model", useCases: ["general"], tier: "S") };
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.InspectRepoAsync("org/no-blocks-model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/no-blocks-model-GGUF", File("Q4_K_M", paramCountB: 1, blockCount: null))));
        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, GpuProfile(64 * Gb), Empty, CancellationToken.None);

        var candidate = result.Recommended.Concat(result.CanRun).Single(c => c.Entry.Id == "no-blocks-model");
        AssertEx.Null(candidate.KvQuantAdvisory, "an incomplete-metadata file must not carry a KV-quant advisory.");
    }

    [Test]
    public async Task BuildRecommendationsAsync_DoesNotFitAtFp16_ProducesNoCandidate_EvenIfQ8WouldFit()
    {
        // A large-KV dense model that overflows a 4.5GB budget at fp16 (~5.8GB) but would fit with a Q8_0 KV cache
        // (~3.6GB). Membership is fp16-only, so the candidate must still be excluded — the advisory never rescues it.
        var entries = new[] { Entry("big-kv-model", useCases: ["general"], tier: "S") };
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.InspectRepoAsync("org/big-kv-model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/big-kv-model-GGUF",
                     File("Q4_K_M", paramCountB: 1, blockCount: 32, attentionHeadCount: 32, attentionHeadCountKV: 8, embeddingLength: 4096))));
        var service = BuildService(entries, discovery, installedTag: "b9692");

        var result = await service.BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 32768, GpuProfile((long)(4.5 * Gb)), Empty, CancellationToken.None);

        AssertEx.Empty(result.Recommended);
        AssertEx.Empty(result.CanRun);
    }

    private static IReadOnlySet<string> Empty { get; } = new HashSet<string>(StringComparer.Ordinal);

    private static ModelCatalogEntry Entry(string id,
        string[] useCases,
        string tier,
        double totalParamsB = 7,
        double? activeParamsB = null,
        bool moe = false,
        string minLlamaCppTag = "b9692",
        string releaseDate = "2026-01-01")
    {
        return new ModelCatalogEntry(id,
            Family: id,
            DisplayName: id,
            Publisher: "Test",
            GgufRepo: $"org/{id}-GGUF",
            License: "mit",
            tier,
            useCases,
            totalParamsB,
            activeParamsB,
            moe,
            ContextLength: 8192,
            minLlamaCppTag,
            releaseDate,
            Notes: null);
    }

    private static CatalogRecommendationService BuildService(IReadOnlyList<ModelCatalogEntry> entries, IHuggingFaceGgufDiscovery discovery, string installedTag)
    {
        var document = new ModelCatalogDocument(SchemaVersion: 1, "test", UpdatedAt: null, entries);
        var snapshot = new ModelCatalogSnapshot(document, ModelCatalogSource.Bundled, FetchedAtUtc: null, SourceUrl: null);
        var catalogProvider = Substitute.For<IModelCatalogProvider>();
        catalogProvider.GetCatalogAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(snapshot));

        var updateState = Substitute.For<ILlamaCppUpdateState>();
        updateState.Current.Returns(new LlamaCppUpdateSnapshot(installedTag, RecommendedTag: null, UpstreamLatestTag: null, UpdateAvailable: false, IsOffline: false, CheckedAtUtc: null));

        return new CatalogRecommendationService(catalogProvider, discovery, new MemoryFitEstimator(), updateState, NullLogger<CatalogRecommendationService>.Instance);
    }

    private static IHuggingFaceGgufDiscovery DiscoveryReturning(IReadOnlyList<ModelCatalogEntry> entries, double paramCountB)
    {
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        foreach (var entry in entries)
        {
            discovery.InspectRepoAsync(entry.GgufRepo, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(Detail(entry.GgufRepo, File("Q4_K_M", paramCountB))));
        }

        return discovery;
    }

    private static GgufRepoDetail Detail(string repoId, params GgufRepoFile[] files)
    {
        return new GgufRepoDetail(repoId, IsGated: false, "mit", files);
    }

    private static GgufRepoFile File(string quant,
        double paramCountB,
        long? blockCount = 4,
        long? attentionHeadCount = 4,
        long? attentionHeadCountKV = 2,
        long? embeddingLength = 16)
    {
        // Default: a small, fits-anywhere geometry (4 layers, 2 kv-heads, embedding 16 over 4 heads) so only param-count
        // drives weights. Callers override the geometry to force a large KV term or to null a header field (insufficient
        // metadata), which drives the KV-quant-advisory tests.
        var paramCount = (long)(paramCountB * 1_000_000_000d);
        return new GgufRepoFile($"model.{quant}.gguf",
            quant,
            SizeBytes: 1 * Gb,
            Sha256: null,
            "main",
            "llama",
            quant,
            paramCount,
            blockCount,
            attentionHeadCount,
            attentionHeadCountKV,
            embeddingLength,
            ContextLength: 8192);
    }

    private static HardwareProfile GpuProfile(long vramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
